namespace ClaudeWidget.Core.Settings;

/// <summary>
/// Wpisy widżetu w ~/.claude/settings.json: hooki i statusline. Rusza wyłącznie wpisy widżetu —
/// także te z wersji z Node (.claude/widget/hook.mjs, .claude/widget/statusline.mjs) — a przed
/// zapisem zostawia kopię pliku obok.
/// </summary>
public static class ClaudeSettings
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>
    /// Dopisuje hooki i statusline wskazujące na <paramref name="hookExePath"/>, najpierw zdejmując
    /// wszystkie dawne wpisy widżetu. Cudzej statusline nie nadpisuje.
    /// </summary>
    /// <returns>true, gdy zostawiono cudzą statusline (widżet nie dostanie wtedy limitów ani kontekstu).</returns>
    public static bool Install(string settingsPath, string hookExePath) => throw new NotImplementedException();

    /// <summary>Usuwa wszystkie wpisy widżetu, aż do pustych grup i pustego obiektu hooks.</summary>
    public static void Remove(string settingsPath) => throw new NotImplementedException();
}
