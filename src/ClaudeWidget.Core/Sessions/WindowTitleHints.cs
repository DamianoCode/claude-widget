using System.Text.Json;

namespace ClaudeWidget.Core.Sessions;

/// <summary>Okno IDE połączonego z Claude Code: PID procesu IDE i otwarte w nim foldery.</summary>
public sealed record IdeWorkspace(int Pid, IReadOnlyList<string> Folders);

/// <summary>
/// Fragmenty tytułu, po których wybiera się okno sesji spośród okien jej procesu-hosta — od
/// najpewniejszego. VS Code trzyma wszystkie okna w jednym procesie, a jego tytuł to
/// „plik - folder - Visual Studio Code”, więc najpewniejszy jest folder workspace, w którym leży
/// katalog sesji. Nazwa sesji i projektu zostają dla terminali (Windows Terminal) i dla sesji
/// spoza workspace.
/// </summary>
public static class WindowTitleHints
{
    public static IReadOnlyList<string> For(SessionInfo session, IReadOnlyList<IdeWorkspace> workspaces, int hostPid)
    {
        var hints = new List<string>();
        // Najgłębszy folder, bo w wielofolderowym workspace foldery bywają zagnieżdżone.
        var folder = workspaces
            .Where(workspace => workspace.Pid == hostPid)
            .SelectMany(workspace => workspace.Folders)
            .Where(candidate => IsInside(session.Cwd, candidate))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
        Add(hints, folder is null ? null : Path.GetFileName(Normalize(folder)));
        Add(hints, session.Name);
        Add(hints, session.Project);
        return hints;
    }

    public static bool MatchesAny(string title, IReadOnlyList<string> hints) =>
        hints.Any(hint => title.Contains(hint, StringComparison.OrdinalIgnoreCase));

    // Katalog sesji to sam folder albo jego podfolder; „emx-monorepo2” nie leży w „emx-monorepo”.
    internal static bool IsInside(string cwd, string folder)
    {
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(folder)) return false;
        var inner = Normalize(cwd);
        var outer = Normalize(folder);
        return inner.Equals(outer, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static void Add(List<string> hints, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && !hints.Contains(hint, StringComparer.OrdinalIgnoreCase)) hints.Add(hint);
    }
}

/// <summary>
/// Pliki <c>&lt;port&gt;.lock</c>, które IDE z rozszerzeniem Claude Code zapisuje w katalogu
/// <c>ide</c> konfiguracji Claude Code. Widżet bierze z nich tylko PID i foldery — token połączenia
/// go nie interesuje.
/// </summary>
public static class IdeLocks
{
    public static string DefaultDir()
    {
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return Path.Combine(string.IsNullOrWhiteSpace(config)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : config, "ide");
    }

    public static IReadOnlyList<IdeWorkspace> Read(string directory)
    {
        var workspaces = new List<IdeWorkspace>();
        if (!Directory.Exists(directory)) return workspaces;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory, "*.lock").ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return workspaces; }

        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var pidValue)) continue;
                if (!root.TryGetProperty("workspaceFolders", out var folders) || folders.ValueKind != JsonValueKind.Array) continue;
                var paths = folders.EnumerateArray()
                    .Where(folder => folder.ValueKind == JsonValueKind.String)
                    .Select(folder => folder.GetString()!)
                    .ToList();
                workspaces.Add(new IdeWorkspace(pidValue, paths));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // plik w trakcie zapisu albo nie z tego świata — pomija się go
            }
        }
        return workspaces;
    }
}
