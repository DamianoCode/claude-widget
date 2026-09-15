namespace ClaudeWidget.Core.Agents;

/// <summary>
/// Szuka <c>claude</c> na PATH tak, jak zrobiłaby to powłoka: w kolejności katalogów, a w katalogu —
/// w kolejności rozszerzeń z PATHEXT. Bez uruchamiania powłoki co kilka sekund. Port <c>findClaude</c>
/// z agents.mjs.
/// </summary>
public static class ClaudeLocator
{
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".cmd", ".bat" };

    public static string? FindClaude(Func<string, bool> exists, string? path, string? pathExt, bool isWindows = true)
    {
        if (!isWindows) return "claude";

        var extensions = (pathExt ?? ".EXE;.CMD")
            .Split(';')
            .Select(extension => extension.Trim().ToLowerInvariant())
            .Where(Runnable.Contains)
            .ToList();

        foreach (var rawEntry in (path ?? "").Split(';'))
        {
            var dir = rawEntry.Trim();
            if (dir.Length >= 2 && dir[0] == '"' && dir[^1] == '"') dir = dir[1..^1];
            if (dir.Length == 0) continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, $"claude{extension}");
                if (exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
