namespace ClaudeWidget.Core;

public enum SessionFileKind { State, Usage, Seen }

/// <summary>
/// Gdzie leży stan widżetu. Te same ścieżki co w wersji z Node i PowerShellem, więc znaczniki
/// przejrzenia, pozycja okna i stan otwartych sesji przechodzą przez aktualizację.
/// Każda sesja ma osobny plik na piszącego (hook: state, statusline: usage, widżet: seen),
/// więc nikt nikomu nie nadpisuje pól.
/// </summary>
public sealed class WidgetPaths
{
    public const string StateDirVariable = "CLAUDE_WIDGET_STATE_DIR";

    public WidgetPaths(string stateDir)
    {
        StateDir = Path.GetFullPath(stateDir);
    }

    /// <summary>Katalog stanu z CLAUDE_WIDGET_STATE_DIR, a domyślnie ~/.claude/widget/state.</summary>
    public static WidgetPaths FromEnvironment()
    {
        var custom = Environment.GetEnvironmentVariable(StateDirVariable);
        return new WidgetPaths(string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "widget", "state")
            : custom);
    }

    public string StateDir { get; }

    public string WidgetDir => Path.GetDirectoryName(StateDir) ?? StateDir;

    public string LimitsFile => Path.Combine(StateDir, "limits.json");

    public string ConfigFile => Path.Combine(WidgetDir, "widget-config.json");

    public string LogFile => Path.Combine(WidgetDir, "widget.log");

    /// <summary>
    /// Plik sesji danego rodzaju. Id sesji staje się nazwą pliku, więc zostają w nim tylko litery
    /// i cyfry ASCII, '_' i '-'; id bez żadnego z nich daje null.
    /// </summary>
    public string? SessionFile(string? sessionId, SessionFileKind kind)
    {
        var safe = new string((sessionId ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (safe.Length == 0) return null;
        var suffix = kind switch
        {
            SessionFileKind.State => "state",
            SessionFileKind.Usage => "usage",
            _ => "seen",
        };
        return Path.Combine(StateDir, $"{safe}.{suffix}.json");
    }
}
