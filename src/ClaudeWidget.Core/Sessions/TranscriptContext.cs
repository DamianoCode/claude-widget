using System.Text;
using System.Text.Json;

namespace ClaudeWidget.Core.Sessions;

/// <summary>Zajętość kontekstu odczytana z transkryptu: tokeny ostatniej odpowiedzi i model.</summary>
public sealed record TranscriptUsage(double Tokens, string? Model);

/// <summary>
/// Kontekst sesji z jej transkryptu — dla sesji bez statusline (Orca, Agent SDK, IDE, sesje w tle).
/// Ostatnia odpowiedź głównej rozmowy ma <c>usage</c>; wejście (z cache) to dokładnie to, co mieści
/// teraz kontekst — z tej samej sumy Claude Code liczy procent w statusline.
/// Transkrypt to format wewnętrzny Claude Code: czego nie da się odczytać, tego nie pokazujemy.
/// </summary>
public sealed class TranscriptContext
{
    // Zwykle ostatnia odpowiedź jest blisko końca; gdy po niej przyszedł duży wynik narzędzia,
    // druga próba sięga dalej.
    private const int TailBytes = 512 * 1024;
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, TranscriptUsage? Result)> _cache = [];

    /// <summary>Kontekst z transkryptu albo null; plik czyta się tylko po zmianie.</summary>
    public TranscriptUsage? Read(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath) || !transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = new FileInfo(transcriptPath);
            if (!file.Exists) return null;
            if (_cache.TryGetValue(transcriptPath, out var cached) && cached.Length == file.Length && cached.WrittenUtc == file.LastWriteTimeUtc)
            {
                return cached.Result;
            }
            var result = Find(ReadTail(file, TailBytes), out var decided);
            if (!decided && file.Length > TailBytes) result = Find(ReadTail(file, MaxBytes), out _);
            _cache[transcriptPath] = (file.Length, file.LastWriteTimeUtc, result);
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Zapomina transkrypty sesji, których już nie ma na liście.</summary>
    public void Retain(IReadOnlySet<string> transcriptPaths)
    {
        foreach (var path in _cache.Keys.Where(path => !transcriptPaths.Contains(path)).ToList()) _cache.Remove(path);
    }

    /// <summary>
    /// Szuka od końca ostatniej odpowiedzi głównej rozmowy z <c>usage</c> (bez podagentów).
    /// Streszczenie rozmowy (compact) po ostatniej odpowiedzi oznacza, że dawny kontekst już nie
    /// obowiązuje, a nowego jeszcze nie znamy — wtedy null.
    /// </summary>
    /// <param name="decided">false, gdy we fragmencie nie było ani odpowiedzi, ani streszczenia.</param>
    public static TranscriptUsage? Find(IReadOnlyList<string> lines, out bool decided)
    {
        decided = true;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            var assistant = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
            var compact = !assistant && line.Contains("\"compact_boundary\"", StringComparison.Ordinal);
            if (!assistant && !compact) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                var type = Text(root, "type");
                if (type == "system" && Text(root, "subtype") == "compact_boundary") return null;
                if (type != "assistant") continue;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                var model = Text(message, "model");
                // Wiadomości wstawiane przez samo Claude Code (np. błąd API) nie mają prawdziwego usage.
                if (model == "<synthetic>") continue;
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                var tokens = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
                if (tokens <= 0) continue;
                return new TranscriptUsage(tokens, model);
            }
            catch (JsonException)
            {
                // Urwana linia na początku odczytanego fragmentu albo coś nowego w formacie.
            }
        }
        decided = false;
        return null;
    }

    /// <summary>
    /// Transkrypt sesji, której hook nie podał ścieżki (np. sesja w tle z listy agentów):
    /// <c>&lt;konfiguracja&gt;/projects/&lt;katalog z myślnikami&gt;/&lt;sesja&gt;.jsonl</c>.
    /// </summary>
    public static string? GuessPath(string configDir, string? cwd, string? sessionId)
    {
        if (string.IsNullOrEmpty(cwd) || string.IsNullOrEmpty(sessionId)) return null;
        if (!sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        var folder = new string(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(configDir, "projects", folder, sessionId + ".jsonl");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Max(0, number)
            : 0;

    private static List<string> ReadTail(FileInfo file, int bytes)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - bytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // Pierwsza linia fragmentu jest zwykle urwana w połowie.
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
        return lines;
    }
}
