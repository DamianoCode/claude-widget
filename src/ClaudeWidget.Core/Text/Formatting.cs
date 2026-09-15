using System.Globalization;

namespace ClaudeWidget.Core.Text;

/// <summary>
/// Formatowanie czasu, liczb sesji i tokenów po polsku — bez zależności od modelu sesji,
/// żeby dało się go użyć wszędzie (limity, sesje, panel).
/// </summary>
public static class Formatting
{
    private static readonly string[] Days = ["nd", "pn", "wt", "śr", "czw", "pt", "sob"];

    /// <summary>Odstęp czasu po polsku: sekundy, minuty, godziny (z minutami) albo dni.</summary>
    public static string FormatSpan(double ms)
    {
        var seconds = (long)Math.Floor(Math.Max(0, ms) / 1000);
        if (seconds < 60) return $"{seconds} s";
        if (seconds < 3600) return $"{seconds / 60} min";
        if (seconds < 86400)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            return minutes > 0 ? $"{hours} h {minutes} min" : $"{hours} h";
        }
        return $"{seconds / 86400} d";
    }

    /// <summary>Liczba sesji z poprawną odmianą: 1 sesja, 2-4 sesje (poza 12-14), reszta sesji.</summary>
    public static string FormatSessions(int count)
    {
        string word;
        if (count == 1) word = "sesja";
        else
        {
            var mod10 = count % 10;
            var mod100 = count % 100;
            word = mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "sesje" : "sesji";
        }
        return $"{count} {word}";
    }

    /// <summary>Liczba tokenów: powyżej miliona jako "1,2 mln", inaczej jako pełne tysiące.</summary>
    public static string FormatTokens(double tokens)
    {
        if (tokens >= 1_000_000)
        {
            var millions = Math.Round(tokens / 1_000_000, 1, MidpointRounding.AwayFromZero);
            var text = millions == Math.Floor(millions)
                ? millions.ToString("0", CultureInfo.InvariantCulture)
                : millions.ToString("0.#", CultureInfo.InvariantCulture);
            return $"{text} mln";
        }
        return $"{Math.Round(tokens / 1000, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} tys.";
    }

    public static DateTime GetLocalTime(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;

    /// <summary>Data bez roku: "dd.MM", jak w opisie resetu limitu tygodniowego.</summary>
    public static string FormatDate(double ms) => GetLocalTime(ms).ToString("dd.MM", CultureInfo.InvariantCulture);

    /// <summary>Chwila w czasie lokalnym: "HH:mm", a z dniem tygodnia — "wt HH:mm".</summary>
    public static string FormatMoment(double ms, bool withDay)
    {
        var at = GetLocalTime(ms);
        return withDay ? $"{Days[(int)at.DayOfWeek]} {at:HH:mm}" : at.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"stan z HH:mm" dzisiaj, a starszy odczyt — z datą.</summary>
    public static string GetFreshnessText(long? updatedAt)
    {
        if (updatedAt is not long ms) return "brak danych o limitach";
        var at = GetLocalTime(ms);
        return at.Date == DateTime.Now.Date
            ? $"stan z {at:HH:mm}"
            : $"stan z {at.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Czas czekania w pełnych minutach ("&lt;1 min" poniżej minuty), żeby tekst nie zmieniał się
    /// co sekundę — inaczej wiersz z sesją przebudowywałby się bez przerwy.
    /// </summary>
    public static string GetWaitSpan(long since, long nowMs)
    {
        var elapsed = nowMs - since;
        return elapsed < 60000 ? "<1 min" : FormatSpan(elapsed);
    }
}
