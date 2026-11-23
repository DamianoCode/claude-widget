namespace ClaudeWidget.Core.Limits;

/// <summary>Wynik jednej próby pobrania limitów z serwera.</summary>
public enum UsagePollOutcome
{
    /// <summary>Limity zapisane.</summary>
    Ok,

    /// <summary>Świeże limity już są, a pełny odczyt serwera nie jest starszy niż <see cref="UsagePollPolicy.ServerInterval"/>.</summary>
    Fresh,

    /// <summary>Brak tokenu, token wygasł albo serwer już go odrzucił — czekamy na nowy od Claude Code.</summary>
    WaitingForToken,

    /// <summary>Serwer ogranicza liczbę zapytań (429).</summary>
    RateLimited,

    /// <summary>Sieć, błąd serwera albo odpowiedź bez limitów.</summary>
    Failed,
}

/// <summary>
/// Kiedy pytać serwer o limity. Endpoint ma własny limit zapytań (ten sam token odpytuje też
/// Orca czy <c>/usage</c>), więc pytamy rzadko. Przy aktywnej statusline — co <see cref="ServerInterval"/>,
/// tylko po to, czego ona nie zna (plan, limity modeli).
/// </summary>
public static class UsagePollPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Tyle po resecie okna pytamy ponownie, żeby pasek nie wisiał na „po resecie”.</summary>
    public static readonly TimeSpan AfterReset = TimeSpan.FromSeconds(30);

    /// <summary>Sprawdzenie pliku z tokenem jest lokalne i tanie — bez zapytania do serwera.</summary>
    public static readonly TimeSpan TokenRecheck = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan RateLimitDefault = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Czy odczyt w <c>limits.json</c> (z dowolnego źródła) jest na tyle świeży, że nie trzeba pytać.
    /// Reset okna, który już minął, unieważnia odczyt — procenty są wtedy nieaktualne.
    /// </summary>
    public static bool IsFresh(AccountLimits? limits, long nowMs)
    {
        if (limits is not { UpdatedAt: > 0 }) return false;
        if (nowMs - limits.UpdatedAt >= Interval.TotalMilliseconds) return false;
        return EarliestResetMs(limits) is not long reset || reset > nowMs || limits.UpdatedAt > reset;
    }

    /// <summary>
    /// Pełny odczyt z serwera (plan, limity modeli) odświeżamy co tyle także wtedy, gdy statusline
    /// dostarcza świeże 5 h i tydzień — ona nie zna limitów pojedynczych modeli.
    /// </summary>
    public static readonly TimeSpan ServerInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Czy nie trzeba pytać: świeży <c>limits.json</c> (z dowolnego źródła) i pełny odczyt serwera
    /// nie starszy niż <see cref="ServerInterval"/>, bez resetu limitu modelu, który od tamtej pory minął.
    /// </summary>
    public static bool IsFresh(AccountLimits? limits, ServerLimits? server, long nowMs)
    {
        if (!IsFresh(limits, nowMs)) return false;
        if (server is not { FetchedAt: > 0 } || nowMs - server.FetchedAt >= ServerInterval.TotalMilliseconds) return false;
        return server.Scoped is not { } scoped || !scoped.Any(limit =>
            limit.ResetsAt is double seconds && double.IsFinite(seconds)
            && (long)(seconds * 1000) is var reset && reset <= nowMs && reset > server.FetchedAt);
    }

    /// <summary>Opóźnienie do następnej próby.</summary>
    /// <param name="limits">Aktualny stan <c>limits.json</c> (po ewentualnym zapisie).</param>
    /// <param name="failures">Kolejne nieudane próby, licząc tę (dla <see cref="UsagePollOutcome.Failed"/>).</param>
    /// <param name="server">Pełny odczyt serwera — jego wiek i resety limitów modeli też wyznaczają termin.</param>
    public static TimeSpan NextDelay(UsagePollOutcome outcome, AccountLimits? limits, long nowMs, int failures = 0, TimeSpan? retryAfter = null, ServerLimits? server = null)
    {
        switch (outcome)
        {
            case UsagePollOutcome.WaitingForToken:
                return TokenRecheck;
            case UsagePollOutcome.RateLimited:
                return Clamp(retryAfter ?? RateLimitDefault, TokenRecheck, TimeSpan.FromHours(1));
            case UsagePollOutcome.Failed:
                var backoff = TimeSpan.FromMinutes(Math.Pow(2, Math.Clamp(failures - 1, 0, 10)));
                return Clamp(backoff, TimeSpan.FromMinutes(1), MaxBackoff);
        }

        // Ok i Fresh: do najbliższego z terminów — zestarzeje się limits.json, zestarzeje się pełny
        // odczyt serwera albo minie reset któregoś limitu (konta lub modelu).
        var delay = Interval;
        if (limits is { UpdatedAt: > 0 })
        {
            delay = TimeSpan.FromMilliseconds(Math.Max(0, limits.UpdatedAt + Interval.TotalMilliseconds - nowMs));
        }
        if (server is { FetchedAt: > 0 })
        {
            var serverDue = TimeSpan.FromMilliseconds(Math.Max(0, server.FetchedAt + ServerInterval.TotalMilliseconds - nowMs));
            if (serverDue < delay) delay = serverDue;
        }
        var resets = (limits is null ? [] : ResetsMs(limits)).Concat(ScopedResetsMs(server));
        if (resets.Where(reset => reset > nowMs).DefaultIfEmpty(long.MaxValue).Min() is var next && next != long.MaxValue)
        {
            var untilReset = TimeSpan.FromMilliseconds(next - nowMs) + AfterReset;
            if (untilReset < delay) delay = untilReset;
        }
        return Clamp(delay, MinDelay, Interval);
    }

    private static IEnumerable<long> ResetsMs(AccountLimits limits) =>
        new[] { limits.FiveHour, limits.SevenDay }
            .Where(window => window?.ResetsAt is double seconds && double.IsFinite(seconds))
            .Select(window => (long)(window!.ResetsAt!.Value * 1000));

    private static IEnumerable<long> ScopedResetsMs(ServerLimits? server) =>
        (server?.Scoped ?? [])
            .Where(limit => limit.ResetsAt is double seconds && double.IsFinite(seconds))
            .Select(limit => (long)(limit.ResetsAt!.Value * 1000));

    private static long? EarliestResetMs(AccountLimits limits)
    {
        long? earliest = null;
        foreach (var window in new[] { limits.FiveHour, limits.SevenDay })
        {
            if (window?.ResetsAt is double seconds && double.IsFinite(seconds))
            {
                var ms = (long)(seconds * 1000);
                if (earliest is null || ms < earliest) earliest = ms;
            }
        }
        return earliest;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
