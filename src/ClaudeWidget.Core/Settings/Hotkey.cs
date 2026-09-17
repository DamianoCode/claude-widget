namespace ClaudeWidget.Core.Settings;

/// <summary>
/// Globalny skrót klawiszowy w postaci tekstu, np. <c>Ctrl+Alt+K</c>: modyfikatory (Ctrl, Alt,
/// Shift, Win) i jeden klawisz — litera, cyfra albo F1–F24. Bez modyfikatora skrót przejąłby
/// zwykłe pisanie, więc taki się odrzuca.
/// </summary>
public readonly record struct Hotkey(uint Modifiers, uint KeyCode)
{
    // Wartości MOD_* i VK_* z Win32 — idą prosto do RegisterHotKey.
    public const uint Alt = 0x1;
    public const uint Control = 0x2;
    public const uint Shift = 0x4;
    public const uint Win = 0x8;

    private static readonly (string Name, uint Flag)[] ModifierNames =
    [
        ("Ctrl", Control), ("Alt", Alt), ("Shift", Shift), ("Win", Win),
    ];

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint modifiers = 0;
        uint? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = ModifierFlag(part);
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }
            if (key is not null || KeyCodeOf(part) is not uint code) return false;
            key = code;
        }
        if (modifiers == 0 || key is null) return false;
        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    /// <summary>Tekst w stałej kolejności modyfikatorów albo null, gdy klawisza nie da się zapisać.</summary>
    public static string? Format(uint modifiers, uint keyCode)
    {
        var name = KeyNameOf(keyCode);
        if (name is null || modifiers == 0) return null;
        var parts = ModifierNames.Where(m => (modifiers & m.Flag) != 0).Select(m => m.Name).Append(name);
        return string.Join('+', parts);
    }

    public override string ToString() => Format(Modifiers, KeyCode) ?? "";

    private static uint ModifierFlag(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" => Control,
        "alt" => Alt,
        "shift" => Shift,
        "win" or "windows" => Win,
        _ => 0,
    };

    private static uint? KeyCodeOf(string part)
    {
        if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) return char.ToUpperInvariant(part[0]);
        if (part.Length >= 2 && part[0] is 'F' or 'f' && int.TryParse(part[1..], out var number) && number is >= 1 and <= 24)
        {
            return (uint)(0x70 + number - 1);
        }
        return null;
    }

    private static string? KeyNameOf(uint keyCode) => keyCode switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)keyCode).ToString(),
        >= 0x70 and <= 0x87 => $"F{keyCode - 0x70 + 1}",
        _ => null,
    };
}
