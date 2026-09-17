namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// Który plik zagra dla danego rodzaju powiadomienia. Wybór z ustawień: <see cref="Builtin"/>,
/// <see cref="None"/> albo ścieżka do pliku. Bez wyboru obowiązuje dawny sposób: need.wav / done.wav
/// w ~/.claude/widget/sounds zastępują wbudowane.
/// </summary>
public static class AlertSounds
{
    public const string Builtin = "builtin";
    public const string None = "none";

    /// <summary>Rozszerzenia, które widżet umie odtworzyć.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".wav", ".mp3", ".wma", ".m4a"];

    public static string FileName(AlertKind kind) => kind == AlertKind.Waiting ? "need.wav" : "done.wav";

    public static string CustomDir(string widgetDir) => Path.Combine(widgetDir, "sounds");

    public static string BuiltinPath(AlertKind kind, string appDir) => Path.Combine(appDir, "Assets", "Sounds", FileName(kind));

    public static bool IsPlayable(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ścieżka do odtworzenia albo null, gdy ma być cisza. Wybrany plik, którego już nie ma (np. na
    /// odłączonym dysku), ustępuje wbudowanemu — lepiej usłyszeć cokolwiek niż przegapić prośbę.
    /// </summary>
    public static string? Resolve(string? choice, AlertKind kind, string widgetDir, string appDir, Func<string, bool> exists)
    {
        if (choice == None) return null;
        var builtin = BuiltinPath(kind, appDir);
        if (choice == Builtin) return builtin;
        if (string.IsNullOrWhiteSpace(choice))
        {
            var legacy = Path.Combine(CustomDir(widgetDir), FileName(kind));
            return exists(legacy) ? legacy : builtin;
        }
        return exists(choice) ? choice : builtin;
    }
}
