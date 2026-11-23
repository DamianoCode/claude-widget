using System.Globalization;
using System.Text.Json;

namespace ClaudeWidget.Core.Limits;

/// <summary>Stan tokenu logowania Claude Code w <c>.credentials.json</c>.</summary>
public enum TokenStatus
{
    /// <summary>Brak pliku albo logowania kontem (np. rozliczanie kluczem API) — limitów z konta nie ma.</summary>
    Missing,

    /// <summary>Token wygasł; odświeży go dopiero Claude Code przy następnym uruchomieniu.</summary>
    Expired,

    Valid,
}

/// <summary>Token z <c>.credentials.json</c>; <see cref="AccessToken"/> jest tylko przy <see cref="TokenStatus.Valid"/>.</summary>
public sealed record OAuthToken(TokenStatus Status, string? AccessToken = null);

/// <summary>
/// Limity konta wprost z serwera — ten sam endpoint, z którego korzysta panel limitów w Orce.
/// Dzięki temu limity są widoczne także wtedy, gdy żadna sesja nie uruchamia statusline (agenci
/// w Orce, rozszerzenie IDE, tryb <c>-p</c>).
///
/// Token jest tylko czytany: nigdy go nie odświeżamy ani nie zapisujemy — rotacja tokenu
/// odświeżania wylogowałaby Claude Code. Wygasły token czeka, aż Claude Code sam go odnowi.
/// </summary>
public static class OAuthUsage
{
    public const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    public const string BetaHeader = "oauth-2025-04-20";

    /// <summary>Token traktujemy jako wygasły chwilę przed czasem, żeby nie łapać 401 na granicy.</summary>
    private const long ExpirySkewMs = 60_000;

    /// <summary>Katalog konfiguracji Claude Code: <c>CLAUDE_CONFIG_DIR</c>, a domyślnie <c>~/.claude</c>.</summary>
    public static string ConfigDir(string? claudeConfigDirVariable, string userProfile) =>
        string.IsNullOrWhiteSpace(claudeConfigDirVariable) ? Path.Combine(userProfile, ".claude") : claudeConfigDirVariable;

    public static string CredentialsFile(string configDir) => Path.Combine(configDir, ".credentials.json");

    /// <summary>Czyta token z <c>.credentials.json</c>; plik zajęty, uszkodzony lub nieobecny to <see cref="TokenStatus.Missing"/>.</summary>
    public static OAuthToken ReadToken(string credentialsFile, long nowMs)
    {
        try
        {
            using var stream = new FileStream(credentialsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return ParseToken(document.RootElement, nowMs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OAuthToken(TokenStatus.Missing);
        }
    }

    public static OAuthToken ParseToken(JsonElement root, long nowMs)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("claudeAiOauth", out var oauth)
            || oauth.ValueKind != JsonValueKind.Object
            || !oauth.TryGetProperty("accessToken", out var accessToken)
            || accessToken.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            return new OAuthToken(TokenStatus.Missing);
        }

        long? expiresAt = oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var ms)
            ? ms
            : null;
        if (expiresAt is long at && at - ExpirySkewMs <= nowMs) return new OAuthToken(TokenStatus.Expired);
        return new OAuthToken(TokenStatus.Valid, accessToken.GetString());
    }

    /// <summary>
    /// Odpowiedź endpointu jako <see cref="AccountLimits"/> — w tym samym kształcie, który zapisuje
    /// statusline (procent 0–100, reset w sekundach od epoki). Null, gdy nie ma w niej żadnego limitu.
    /// </summary>
    public static AccountLimits? ParseResponse(JsonElement root, long nowMs)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var fiveHour = ParseWindow(root, "five_hour");
        var sevenDay = ParseWindow(root, "seven_day");
        if (fiveHour is null && sevenDay is null) return null;
        return new AccountLimits { FiveHour = fiveHour, SevenDay = sevenDay, UpdatedAt = nowMs, Source = LimitSources.Server };
    }

    /// <summary>
    /// To z odpowiedzi, czego statusline nie poda: czy plan ma okno 5 h i limity pojedynczych modeli.
    /// Lista <c>limits</c> opisuje każdy obowiązujący limit (<c>percent</c> to zużycie); gdy jej nie
    /// ma, zostają dawne pola <c>seven_day_opus</c> / <c>seven_day_sonnet</c>.
    /// </summary>
    public static ServerLimits? ParseServer(JsonElement root, long nowMs)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var hasFiveHour = root.TryGetProperty("five_hour", out var five) && five.ValueKind == JsonValueKind.Object;
        var scoped = new List<ScopedLimit>();
        if (root.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var group = Text(entry, "group");
                var kind = Text(entry, "kind") ?? "";
                var model = entry.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var modelScope) && modelScope.ValueKind == JsonValueKind.Object
                        ? Text(modelScope, "display_name")
                        : null;
                if (model is null)
                {
                    // Ogólny limit okna sesji — dowód, że plan ma okno 5 h. Nieznane grupy nic nie przesądzają.
                    if (group is "session" or "five_hour" || kind.StartsWith("session", StringComparison.Ordinal)
                        || kind.StartsWith("five_hour", StringComparison.Ordinal))
                    {
                        hasFiveHour = true;
                    }
                    continue;
                }
                if (scoped.Any(limit => limit.Label == model && limit.Group == group)) continue;
                scoped.Add(new ScopedLimit
                {
                    Label = model,
                    Group = group,
                    Pct = Number(entry, "percent") is double pct && double.IsFinite(pct) ? Math.Clamp(pct, 0, 100) : null,
                    ResetsAt = entry.TryGetProperty("resets_at", out var reset) ? ResetSeconds(reset) : null,
                });
            }
        }
        else
        {
            foreach (var (key, label) in new[] { ("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet") })
            {
                if (ParseWindow(root, key) is { } window)
                    scoped.Add(new ScopedLimit { Label = label, Group = "weekly", Pct = window.Pct, ResetsAt = window.ResetsAt });
            }
        }
        return new ServerLimits { HasFiveHour = hasFiveHour, Scoped = scoped.Count > 0 ? scoped : null, FetchedAt = nowMs };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static LimitWindow? ParseWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        // Endpoint podaje "utilization"; "used_percentage" to nazwa ze statusline — przyjmujemy obie.
        var pct = Number(window, "utilization") ?? Number(window, "used_percentage");
        if (pct is not double value || !double.IsFinite(value)) return null;
        return new LimitWindow
        {
            Pct = Math.Clamp(value, 0, 100),
            ResetsAt = window.TryGetProperty("resets_at", out var reset) ? ResetSeconds(reset) : null,
        };
    }

    /// <summary>Reset jako sekundy od epoki: przychodzi jako data ISO, sekundy albo milisekundy.</summary>
    internal static double? ResetSeconds(JsonElement reset)
    {
        double? number = reset.ValueKind switch
        {
            JsonValueKind.Number => reset.GetDouble(),
            JsonValueKind.String when double.TryParse(reset.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        if (number is double n) return double.IsFinite(n) && n > 0 ? (n > 1e10 ? n / 1000 : n) : null;

        if (reset.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date.ToUnixTimeMilliseconds() / 1000.0;
        }
        return null;
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
