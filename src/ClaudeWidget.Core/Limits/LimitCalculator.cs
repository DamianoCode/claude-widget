using ClaudeWidget.Core.Text;

namespace ClaudeWidget.Core.Limits;

/// <summary>
/// Limit konta: procent, kolor i opis. Kolor zależy od tempa, nie tylko od procentu — 60% po
/// 4 godzinach okna to spokój, 60% po 30 minutach oznacza, że zabraknie przed resetem.
/// </summary>
public static class LimitCalculator
{
    private static readonly Dictionary<LimitWindowKind, long> WindowMs = new()
    {
        [LimitWindowKind.FiveHour] = 5 * 3_600_000L,
        [LimitWindowKind.SevenDay] = 7 * 86_400_000L,
    };

    public static LimitView GetView(LimitWindow? limit, LimitWindowKind window, long? measuredAt, long nowMs, bool panel)
    {
        if (limit?.Pct is not double pct) return new LimitView();

        var withDay = window == LimitWindowKind.SevenDay;
        long? resetMs = limit.ResetsAt is double resetsAtSeconds ? (long)(resetsAtSeconds * 1000) : null;
        if (resetMs is long resetAtBoundary && nowMs >= resetAtBoundary)
        {
            return new LimitView { Note = "po resecie, czekam na nowe dane" };
        }

        long? exhaustMs = null;
        if (resetMs is long resetAt && measuredAt is long measured && pct > 0)
        {
            var windowMs = WindowMs[window];
            var startMs = resetAt - windowMs;
            var elapsed = (double)(measured - startMs) / windowMs;
            // Na samym początku okna tempo jest jeszcze szumem.
            if (elapsed >= 0.1)
            {
                var hitMs = startMs + (measured - startMs) * 100.0 / pct;
                if (hitMs < resetAt) exhaustMs = (long)hitMs;
            }
        }

        var color = "#D97757";
        string? warn = null;
        if (pct >= 90) { color = "#FF5A4E"; warn = "#FF5A4E"; }
        else if (pct >= 75 || exhaustMs is not null) { color = "#FFB224"; warn = "#FFB224"; }

        var reset = resetMs is long resetValue
            ? panel
                ? withDay
                    ? $"reset {Formatting.FormatDate(resetValue)}, {Formatting.FormatMoment(resetValue, false)}"
                    : $"reset {Formatting.FormatMoment(resetValue, false)} · za {Formatting.FormatSpan(resetValue - nowMs)}"
                : withDay
                    ? $"reset {Formatting.FormatMoment(resetValue, true)}"
                    : $"reset za {Formatting.FormatSpan(resetValue - nowMs)}"
            : "";

        // Na karcie mieści się jedna krótka linia; pełne zdanie z godziną resetu jest w panelu.
        var note = exhaustMs is long hit
            ? !panel
                ? $"skończy się ok. {Formatting.FormatMoment(hit, withDay)}"
                : reset.Length > 0
                    ? $"w tym tempie skończy się ok. {Formatting.FormatMoment(hit, withDay)} · {reset}"
                    : $"w tym tempie skończy się ok. {Formatting.FormatMoment(hit, withDay)}"
            : reset;

        return new LimitView { Pct = pct, Color = color, Note = note, Warn = warn };
    }
}
