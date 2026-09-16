using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace ClaudeWidget;

public enum UpdateCheckResult
{
    /// <summary>Widżet nie jest zainstalowany przez Velopack (uruchomienie deweloperskie).</summary>
    NotInstalled,
    UpToDate,
    /// <summary>Nowa wersja jest pobrana i czeka na restart.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Sprawdza aktualizacje z GitHub Releases, gdy widżet jest zainstalowany przez Velopack (dev runy
/// nigdy nie sprawdzają). Pobiera po cichu i nie przerywa pracy: pobrana wersja instaluje się sama
/// przy następnym uruchomieniu widżetu (VelopackApp.SetAutoApplyOnStartup), a pozycja w menu
/// zasobnika pozwala zrobić to od razu.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager = new(new GithubSource("https://github.com/DamianoCode/claude-widget", null, false));
    // Sprawdzenie z menu może trafić na sprawdzenie okresowe — drugie czeka, zamiast pobierać to samo równolegle.
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>
    /// Wersja do pokazania: zainstalowana według Velopack, a w uruchomieniu deweloperskim — wersja
    /// z kompilacji z dopiskiem „dev” (ten sam numer co ostatnie wydanie, ale inny kod).
    /// </summary>
    public string CurrentVersion
    {
        get
        {
            if (_manager.CurrentVersion is { } installed) return installed.ToString();
            var built = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            // SDK dopisuje „+<hash commita>” — do pokazania wystarczy numer.
            var plus = built.IndexOf('+');
            return (plus >= 0 ? built[..plus] : built) + " (dev)";
        }
    }

    /// <summary>Wersja gotowa do zainstalowania po restarcie, albo null, gdy nic nie czeka.</summary>
    public string? PendingVersion { get; private set; }

    private VelopackAsset? _pendingUpdate;

    public event Action? UpdateReady;

    public async Task StartAsync(Action<string> log, CancellationToken cancellationToken)
    {
        if (!IsInstalled) return;
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckAsync(log).ConfigureAwait(false);
            try { await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(Action<string> log)
    {
        if (!IsInstalled) return UpdateCheckResult.NotInstalled;
        await _checkLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pendingUpdate is not null) return UpdateCheckResult.Ready; // już pobrana, czeka na restart
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null) return UpdateCheckResult.UpToDate;
            await _manager.DownloadUpdatesAsync(info, null, CancellationToken.None).ConfigureAwait(false);
            _pendingUpdate = info.TargetFullRelease;
            PendingVersion = info.TargetFullRelease.Version.ToString();
            UpdateReady?.Invoke();
            return UpdateCheckResult.Ready;
        }
        catch (Exception error)
        {
            log($"sprawdzanie aktualizacji: {error.Message}");
            return UpdateCheckResult.Failed;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>
    /// <paramref name="restartArgs"/> — argumenty startowe (np. <c>--state-dir</c>), żeby widżet po
    /// restarcie otworzył się na tym samym katalogu stanu, niezależnie od tego, jak go uruchomiono.
    /// </summary>
    public void ApplyAndRestart(string[] restartArgs)
    {
        if (_pendingUpdate is not null) _manager.ApplyUpdatesAndRestart(_pendingUpdate, restartArgs);
    }
}
