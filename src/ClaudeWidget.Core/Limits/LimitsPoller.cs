namespace ClaudeWidget.Core.Limits;

/// <summary>
/// Pobiera limity konta z serwera: co 5 minut, gdy statusline ich nie dostarcza — np. pracujesz tylko w Orce,
/// rozszerzeniu IDE albo w trybie <c>-p</c>, a przy działającej statusline rzadziej, po to, czego ona nie
/// zna. 5 h i tydzień trafiają do tego samego <c>limits.json</c>, a pełny odczyt (plan, limity modeli)
/// do <c>limits-server.json</c>. Nie wymaga konfiguracji: używa logowania Claude Code.
///
/// Token nigdy nie trafia do logu, a odrzucony nie jest wysyłany ponownie — nowy pojawi się, gdy
/// Claude Code sam go odświeży.
/// </summary>
public sealed class LimitsPoller : IDisposable
{
    private readonly WidgetPaths _paths;
    private readonly UsageClient _client;
    private readonly Func<long, OAuthToken> _readToken;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly IDisposable? _owned;
    private readonly CancellationTokenSource _stop = new();
    private string? _rejectedToken;
    private string? _lastReported;
    private int _failures;

    public LimitsPoller(WidgetPaths paths, UsageClient client, Func<long, OAuthToken> readToken, TimeProvider time, Action<string> log)
        : this(paths, client, readToken, time, log, owned: null)
    {
    }

    private LimitsPoller(WidgetPaths paths, UsageClient client, Func<long, OAuthToken> readToken, TimeProvider time, Action<string> log, IDisposable? owned)
    {
        _paths = paths;
        _client = client;
        _readToken = readToken;
        _time = time;
        _log = log;
        _owned = owned;
    }

    /// <summary>Poller na prawdziwym serwerze i tokenie Claude Code z bieżącego środowiska.</summary>
    public static LimitsPoller Create(WidgetPaths paths, string userAgent, Action<string> log)
    {
        var http = UsageClient.CreateHttpClient();
        var credentials = OAuthUsage.CredentialsFile(OAuthUsage.ConfigDir(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var time = TimeProvider.System;
        return new LimitsPoller(paths, new UsageClient(http, userAgent, time), nowMs => OAuthUsage.ReadToken(credentials, nowMs), time, log, http);
    }

    /// <summary>Uruchamia pętlę w tle; kończy ją <see cref="Dispose"/>.</summary>
    public void Start() => _ = Task.Run(() => RunAsync(_stop.Token));

    public void Dispose()
    {
        _stop.Cancel();
        _owned?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var delay = await PollOnceAsync(cancellation).ConfigureAwait(false);
                await Delay(delay, cancellation).ConfigureAwait(false);
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // Ostatnia siatka (np. błąd odczytu pliku stanu): pętla żyje dalej, w zwykłym rytmie.
                Report($"nieoczekiwany błąd ({error.GetType().Name})");
                await Delay(UsagePollPolicy.Interval, cancellation).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Czekanie przerwane zamknięciem widżetu nie jest błędem.</summary>
    private async Task Delay(TimeSpan delay, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(delay, _time, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>Jedna próba: pyta serwer, jeśli trzeba, i zwraca opóźnienie do następnej.</summary>
    internal async Task<TimeSpan> PollOnceAsync(CancellationToken cancellation)
    {
        var (outcome, retryAfter) = await TryPollAsync(cancellation).ConfigureAwait(false);
        _failures = outcome == UsagePollOutcome.Failed ? _failures + 1 : 0;
        var limits = JsonStore.Read(_paths.LimitsFile, StateJson.Default.AccountLimits);
        var server = JsonStore.Read(_paths.ServerLimitsFile, StateJson.Default.ServerLimits);
        return UsagePollPolicy.NextDelay(outcome, limits, NowMs(), _failures, retryAfter, server);
    }

    private async Task<(UsagePollOutcome Outcome, TimeSpan? RetryAfter)> TryPollAsync(CancellationToken cancellation)
    {
        var startedAt = NowMs();
        if (UsagePollPolicy.IsFresh(
                JsonStore.Read(_paths.LimitsFile, StateJson.Default.AccountLimits),
                JsonStore.Read(_paths.ServerLimitsFile, StateJson.Default.ServerLimits),
                startedAt))
        {
            return (UsagePollOutcome.Fresh, null);
        }

        var token = _readToken(startedAt);
        if (token.Status != TokenStatus.Valid || token.AccessToken is not { } accessToken)
        {
            Report(token.Status == TokenStatus.Expired
                ? "token wygasł, czekam aż Claude Code go odświeży"
                : "brak logowania Claude Code, zostają dane ze statusline");
            return (UsagePollOutcome.WaitingForToken, null);
        }
        if (accessToken == _rejectedToken)
        {
            return (UsagePollOutcome.WaitingForToken, null);
        }

        UsageFetchResult result;
        try
        {
            result = await _client.FetchAsync(accessToken, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested)
        {
            // Każdy błąd zapytania to nieudana próba z backoffem — nigdy koniec pętli. Do logu idzie
            // sam typ: treść wyjątku mogłaby zawierać fragment zapytania.
            Report(error is HttpRequestException or TaskCanceledException or IOException
                ? $"brak połączenia ({error.GetType().Name})"
                : $"nieoczekiwany błąd ({error.GetType().Name})");
            return (UsagePollOutcome.Failed, null);
        }

        switch (result.Status)
        {
            case UsageFetchStatus.Ok:
                // Osobno: nieudany zapis pełnego odczytu (zgłoszony w logu) nie blokuje limitów 5 h i tygodnia.
                SaveServer(result.Server);
                if (!Save(result.Limits!, startedAt)) return (UsagePollOutcome.Failed, null);
                Report("działa");
                return (UsagePollOutcome.Ok, null);
            case UsageFetchStatus.Rejected:
                _rejectedToken = accessToken;
                Report($"{result.Detail}, czekam na nowy token od Claude Code");
                return (UsagePollOutcome.WaitingForToken, null);
            case UsageFetchStatus.RateLimited:
                Report(result.Detail!);
                return (UsagePollOutcome.RateLimited, result.RetryAfter);
            default:
                Report(result.Detail!);
                return (UsagePollOutcome.Failed, null);
        }
    }

    /// <summary>Pełny odczyt serwera; ten plik pisze tylko widżet, więc zawsze nadpisuje. False, gdy zapis się nie udał.</summary>
    private bool SaveServer(ServerLimits? server)
    {
        if (server is null) return true;
        try
        {
            JsonStore.Write(_paths.ServerLimitsFile, server, StateJson.Default.ServerLimits);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report($"nie udało się zapisać limits-server.json ({error.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Zapisuje odczyt, chyba że statusline zapisała w trakcie zapytania nowszy — ten ma pierwszeństwo.
    /// False, gdy zapis się nie udał.
    /// </summary>
    private bool Save(AccountLimits limits, long startedAt)
    {
        var current = JsonStore.Read(_paths.LimitsFile, StateJson.Default.AccountLimits);
        if (current is { } existing && existing.UpdatedAt >= startedAt) return true;
        try
        {
            JsonStore.Write(_paths.LimitsFile, limits, StateJson.Default.AccountLimits);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Plik chwilowo zablokowany: odczyt nie przepada na zawsze, następna próba zapisze nowszy.
            Report($"nie udało się zapisać limits.json ({error.GetType().Name})");
            return false;
        }
    }

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Loguje tylko zmianę stanu, żeby co 5 minut nie dopisywać tej samej linii.</summary>
    private void Report(string message)
    {
        if (message == _lastReported) return;
        _lastReported = message;
        _log($"limity z serwera: {message}");
    }
}
