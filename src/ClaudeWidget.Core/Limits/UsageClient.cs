using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeWidget.Core.Limits;

public enum UsageFetchStatus
{
    Ok,

    /// <summary>401/403 — token nieważny albo bez uprawnień; ten sam token nie przejdzie drugi raz.</summary>
    Rejected,

    /// <summary>429 — serwer ogranicza liczbę zapytań.</summary>
    RateLimited,

    /// <summary>Inny kod, odpowiedź bez limitów albo nie-JSON.</summary>
    Failed,
}

/// <param name="Server">Pełny odczyt (plan, limity modeli) — obok <paramref name="Limits"/> w kształcie statusline.</param>
public sealed record UsageFetchResult(UsageFetchStatus Status, AccountLimits? Limits = null, TimeSpan? RetryAfter = null, string? Detail = null, ServerLimits? Server = null);

/// <summary>
/// Jedno zapytanie do endpointu limitów. Wyjątki sieciowe (brak sieci, przekroczony czas) przechodzą
/// do wołającego — to nie jest odpowiedź serwera. Odpowiedź czytana jest w całości w ramach
/// <see cref="HttpClient.Timeout"/>, więc zawieszony serwer nie zatrzyma pętli.
/// </summary>
public sealed class UsageClient(HttpClient http, string userAgent, TimeProvider time)
{
    /// <summary>
    /// Klient bez przekierowań: token idzie wyłącznie na <see cref="OAuthUsage.Endpoint"/>, a nie pod
    /// adres, który podałaby odpowiedź.
    /// </summary>
    public static HttpClient CreateHttpClient() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    internal static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false };

    public async Task<UsageFetchResult> FetchAsync(string accessToken, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OAuthUsage.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", OAuthUsage.BetaHeader);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                break;
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return new UsageFetchResult(UsageFetchStatus.Rejected, Detail: $"odmowa ({code})");
            case HttpStatusCode.TooManyRequests:
                return new UsageFetchResult(UsageFetchStatus.RateLimited, RetryAfter: RetryAfter(response.Headers.RetryAfter), Detail: "za dużo zapytań");
            default:
                return new UsageFetchResult(UsageFetchStatus.Failed, Detail: $"odpowiedź {code}");
        }

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellation).ConfigureAwait(false);
            var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
            var limits = OAuthUsage.ParseResponse(document.RootElement, nowMs);
            return limits is null
                ? new UsageFetchResult(UsageFetchStatus.Failed, Detail: "odpowiedź bez limitów 5 h i tygodniowego")
                : new UsageFetchResult(UsageFetchStatus.Ok, limits, Server: OAuthUsage.ParseServer(document.RootElement, nowMs));
        }
        catch (JsonException)
        {
            return new UsageFetchResult(UsageFetchStatus.Failed, Detail: "odpowiedź nie jest JSON-em");
        }
    }

    private TimeSpan? RetryAfter(RetryConditionHeaderValue? header) => header switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - time.GetUtcNow(),
        _ => null,
    };
}
