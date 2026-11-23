namespace ClaudeWidget.Core.Limits;

/// <summary>Co pokazuje pasek limitu: ile zużyto (domyślnie) albo ile zostało do resetu.</summary>
public enum LimitDisplayMode
{
    Used,
    Left,
}

/// <summary>
/// Przeliczenie zużycia na to, co widać. Oba źródła (statusline i serwer) podają zużycie 0–100;
/// kolory, ostrzeżenia i prognoza zawsze liczą się od zużycia — tryb zmienia tylko wyświetlaną liczbę.
/// </summary>
public static class LimitDisplay
{
    public const string UsedKey = "used";
    public const string LeftKey = "left";

    public static LimitDisplayMode Parse(string? value) =>
        string.Equals(value?.Trim(), LeftKey, StringComparison.OrdinalIgnoreCase) ? LimitDisplayMode.Left : LimitDisplayMode.Used;

    public static string Key(LimitDisplayMode mode) => mode == LimitDisplayMode.Left ? LeftKey : UsedKey;

    public static LimitDisplayMode Toggle(LimitDisplayMode mode) =>
        mode == LimitDisplayMode.Left ? LimitDisplayMode.Used : LimitDisplayMode.Left;

    /// <summary>
    /// Procent do pokazania, w pełnych punktach. „Zostało” to 100 minus zaokrąglone zużycie, więc
    /// oba tryby zawsze sumują się do 100. Brak danych zostaje brakiem, nigdy „100% zostało”.
    /// </summary>
    public static double? Shown(double? usedPct, LimitDisplayMode mode)
    {
        if (usedPct is not double value || !double.IsFinite(value)) return null;
        var used = Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);
        return mode == LimitDisplayMode.Left ? 100 - used : used;
    }

    /// <summary>Słowo przy liczbie, żeby nie było wątpliwości, co ona znaczy.</summary>
    public static string Word(LimitDisplayMode mode) => mode == LimitDisplayMode.Left ? "zostało" : "zużyte";
}
