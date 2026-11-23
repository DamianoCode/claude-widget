using System.Net;
using System.Text;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Limits;

namespace ClaudeWidget.Tests.Limits;

public sealed class LimitsPollerTests : IDisposable
{
    private const string Token = "sk-ant-oat01-test";
    private const string UsageBody = """
        { "five_hour": { "utilization": 40, "resets_at": "2026-11-20T17:00:00Z" },
          "seven_day": { "utilization": 15, "resets_at": "2026-11-27T07:00:00Z" } }
        """;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("widget-limits-");
    private readonly WidgetPaths _paths;
    private readonly FixedTime _time = new(DateTimeOffset.Parse("2026-11-20T14:00:00Z"));
    private readonly StubHandler _http = new();
    private readonly List<string> _log = [];
    private OAuthToken _token = new(TokenStatus.Valid, Token);

    public LimitsPollerTests() => _paths = new WidgetPaths(Path.Combine(_dir.FullName, "state"));

    public void Dispose() => _dir.Delete(recursive: true);

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private LimitsPoller Poller() =>
        new(_paths, new UsageClient(new HttpClient(_http), "claude-widget/9.9.9", _time), _ => _token, _time, _log.Add);

    private AccountLimits? Saved() => JsonStore.Read(_paths.LimitsFile, StateJson.Default.AccountLimits);

    [Fact]
    public async Task Fetches_and_saves_limits_with_the_expected_request()
    {
        _http.Respond(HttpStatusCode.OK, UsageBody);

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        var request = Assert.Single(_http.Requests);
        Assert.Equal(OAuthUsage.Endpoint, request.Uri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(OAuthUsage.BetaHeader, request.Beta);
        Assert.Equal("claude-widget/9.9.9", request.UserAgent);

        var saved = Saved();
        Assert.Equal(40, saved!.FiveHour!.Pct);
        Assert.Equal(15, saved.SevenDay!.Pct);
        Assert.Equal(NowMs, saved.UpdatedAt);
        Assert.Equal(UsagePollPolicy.Interval, delay);
        Assert.Equal(["limity z serwera: działa"], _log);
    }

    [Fact]
    public async Task Saves_the_full_server_reading_next_to_the_statusline_shape()
    {
        _http.Respond(HttpStatusCode.OK, """
            { "five_hour": null, "seven_day": { "utilization": 35 },
              "limits": [ { "group": "weekly", "percent": 7, "scope": { "model": { "display_name": "Fable" } } } ] }
            """);

        await Poller().PollOnceAsync(CancellationToken.None);

        var server = JsonStore.Read(_paths.ServerLimitsFile, StateJson.Default.ServerLimits)!;
        Assert.False(server.HasFiveHour);
        Assert.Equal(NowMs, server.FetchedAt);
        Assert.Equal(7, Assert.Single(server.Scoped!).Pct);
        Assert.Equal(35, Saved()!.SevenDay!.Pct);
    }

    [Fact]
    public async Task Fresh_statusline_with_stale_server_reading_still_asks_the_server()
    {
        var statusline = new AccountLimits { SevenDay = new LimitWindow { Pct = 3 }, UpdatedAt = NowMs - 60_000 };
        JsonStore.Write(_paths.LimitsFile, statusline, StateJson.Default.AccountLimits);
        JsonStore.Write(_paths.ServerLimitsFile, new ServerLimits { FetchedAt = NowMs - (long)UsagePollPolicy.ServerInterval.TotalMilliseconds },
            StateJson.Default.ServerLimits);
        _http.Respond(HttpStatusCode.OK, UsageBody);

        await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Single(_http.Requests);
        // Odczyt serwera jest nowszy niż ten ze statusline, więc trafia też do limits.json.
        Assert.Equal(NowMs, Saved()!.UpdatedAt);
        Assert.Equal(NowMs, JsonStore.Read(_paths.ServerLimitsFile, StateJson.Default.ServerLimits)!.FetchedAt);
    }

    [Fact]
    public async Task Fresh_statusline_reading_means_no_request()
    {
        var statusline = new AccountLimits { SevenDay = new LimitWindow { Pct = 3 }, UpdatedAt = NowMs - 60_000 };
        JsonStore.Write(_paths.LimitsFile, statusline, StateJson.Default.AccountLimits);
        JsonStore.Write(_paths.ServerLimitsFile, new ServerLimits { FetchedAt = NowMs - 5 * 60_000 }, StateJson.Default.ServerLimits);

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.Equal(statusline, Saved());
        Assert.Equal(TimeSpan.FromMinutes(4), delay);
    }

    [Fact]
    public async Task Does_not_overwrite_a_reading_the_statusline_wrote_during_the_request()
    {
        var newer = new AccountLimits { SevenDay = new LimitWindow { Pct = 99 }, UpdatedAt = NowMs + 1 };
        _http.Respond(HttpStatusCode.OK, UsageBody, beforeResponding: () => JsonStore.Write(_paths.LimitsFile, newer, StateJson.Default.AccountLimits));

        await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(newer, Saved());
    }

    [Theory]
    [InlineData(TokenStatus.Missing, "brak logowania Claude Code")]
    [InlineData(TokenStatus.Expired, "token wygasł")]
    public async Task Without_valid_token_nothing_is_sent(TokenStatus status, string logged)
    {
        _token = new OAuthToken(status);

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.Equal(UsagePollPolicy.TokenRecheck, delay);
        Assert.Contains(logged, Assert.Single(_log));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_token_is_never_sent_again_until_it_changes(HttpStatusCode code)
    {
        var poller = Poller();
        _http.Respond(code, """{ "error": { "message": "secret detail" } }""");

        Assert.Equal(UsagePollPolicy.TokenRecheck, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(UsagePollPolicy.TokenRecheck, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Single(_http.Requests);
        Assert.Null(Saved());

        _token = new OAuthToken(TokenStatus.Valid, "sk-ant-oat01-refreshed");
        _http.Respond(HttpStatusCode.OK, UsageBody);
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, _http.Requests.Count);
        Assert.NotNull(Saved());
    }

    [Fact]
    public async Task Rate_limit_honours_retry_after()
    {
        _http.Respond(HttpStatusCode.TooManyRequests, "", retryAfterSeconds: 420);

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(7), delay);
        Assert.Null(Saved());
    }

    [Fact]
    public async Task Server_errors_back_off_and_success_resets_the_backoff()
    {
        var poller = Poller();
        _http.Respond(HttpStatusCode.InternalServerError, "");
        Assert.Equal(TimeSpan.FromMinutes(1), await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(2), await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(4), await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(["limity z serwera: odpowiedź 500"], _log);

        _http.Respond(HttpStatusCode.OK, UsageBody);
        Assert.Equal(UsagePollPolicy.Interval, await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal("limity z serwera: działa", _log[^1]);
    }

    [Theory]
    [InlineData("<html>maintenance</html>", "nie jest JSON-em")]
    [InlineData("""{ "five_hour": null, "seven_day": null }""", "bez limitów")]
    public async Task Unusable_body_is_a_failure_and_keeps_old_data(string body, string logged)
    {
        var old = new AccountLimits { SevenDay = new LimitWindow { Pct = 7 }, UpdatedAt = NowMs - 3_600_000 };
        JsonStore.Write(_paths.LimitsFile, old, StateJson.Default.AccountLimits);
        _http.Respond(HttpStatusCode.OK, body);

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
        Assert.Equal(old, Saved());
        Assert.Contains(logged, Assert.Single(_log));
    }

    [Fact]
    public async Task Network_failure_is_logged_without_details()
    {
        _http.Throw(new HttpRequestException($"connection to api.anthropic.com failed; Authorization: Bearer {Token}"));

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
        var line = Assert.Single(_log);
        Assert.Equal("limity z serwera: brak połączenia (HttpRequestException)", line);
        Assert.DoesNotContain(Token, line);
    }

    [Fact]
    public async Task Connection_dropped_mid_body_is_a_failure()
    {
        _http.RespondWith(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
        Assert.StartsWith("limity z serwera: brak połączenia", Assert.Single(_log));
    }

    [Fact]
    public async Task Unexpected_error_backs_off_instead_of_stopping_the_loop()
    {
        _http.Throw(new InvalidOperationException("boom"));

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
        Assert.Equal("limity z serwera: nieoczekiwany błąd (InvalidOperationException)", Assert.Single(_log));
    }

    [Fact]
    public async Task Timeout_is_a_failure_not_a_shutdown()
    {
        _http.Throw(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var delay = await Poller().PollOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }

    [Fact]
    public async Task Shutdown_cancellation_propagates()
    {
        using var stop = new CancellationTokenSource();
        _http.Respond(HttpStatusCode.OK, UsageBody, beforeResponding: stop.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Poller().PollOnceAsync(stop.Token));
    }

    [Fact]
    public void Default_client_never_follows_redirects()
    {
        // Przekierowanie wysłałoby nagłówek Authorization pod adres wskazany przez odpowiedź.
        using var handler = UsageClient.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    /// <summary>Strumień, który zrywa się przy pierwszym odczycie — jak połączenie zerwane w trakcie odpowiedzi.</summary>
    private sealed class BrokenStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException("connection reset"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SentRequest(string? Uri, string? Authorization, string? Beta, string UserAgent);

    private sealed class StubHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage>? _respond;

        public List<SentRequest> Requests { get; } = [];

        public void Respond(HttpStatusCode code, string body, int? retryAfterSeconds = null, Action? beforeResponding = null) =>
            _respond = () =>
            {
                beforeResponding?.Invoke();
                var response = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (retryAfterSeconds is int seconds) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
                return response;
            };

        public void Throw(Exception error) => _respond = () => throw error;

        public void RespondWith(Func<HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.RequestUri?.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.Single() : null,
                request.Headers.UserAgent.ToString()));
            var response = _respond!();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response);
        }
    }
}
