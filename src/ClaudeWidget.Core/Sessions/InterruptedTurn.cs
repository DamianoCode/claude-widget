using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// Tura przerwana klawiszem Esc. Claude Code nie wysyła wtedy żadnego zdarzenia hooka (Stop nie
/// przychodzi, a idle_prompt dopiero po minucie bez pisania — więc wcale, gdy od razu piszesz
/// dalej), a sesja świeciłaby na żółto aż do następnego polecenia. W chwili przerwania Claude Code
/// dopisuje jednak do transkryptu wiadomość „[Request interrupted by user…]”.
/// Transkrypt to format wewnętrzny Claude Code: czego nie da się odczytać, to nie jest przerwanie
/// i widżet działa jak dotąd, na samych hookach.
/// </summary>
public sealed class InterruptedTurn
{
    public const string Marker = "[Request interrupted by user";

    // Wpis o przerwaniu jest mały, a po nim są już tylko drobne wpisy techniczne; ogromna ostatnia
    // linia (np. wynik narzędzia) i tak nie jest przerwaniem.
    private const int TailBytes = 128 * 1024;

    private readonly Dictionary<string, (long Length, DateTime WrittenUtc, long Since, long? Result)> _cache = [];

    /// <summary>
    /// Kiedy (ms od epoki) przerwano turę rozpoczętą w <paramref name="turnStartedAt"/>, albo null,
    /// gdy transkrypt nie kończy się przerwaniem. Plik czyta się tylko po zmianie.
    /// </summary>
    public long? Check(string? transcriptPath, long turnStartedAt)
    {
        if (string.IsNullOrEmpty(transcriptPath) || !transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = new FileInfo(transcriptPath);
            if (!file.Exists) return null;
            if (_cache.TryGetValue(transcriptPath, out var cached) && cached.Length == file.Length
                && cached.WrittenUtc == file.LastWriteTimeUtc && cached.Since == turnStartedAt)
            {
                return cached.Result;
            }
            var result = Find(ReadTail(file), turnStartedAt);
            _cache[transcriptPath] = (file.Length, file.LastWriteTimeUtc, turnStartedAt, result);
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
    /// Szuka od końca ostatniej wiadomości rozmowy (użytkownika albo Claude, bez podagentów
    /// i wpisów pomocniczych).
    /// Przerwanie liczy się tylko wtedy, gdy to ona i jest nie starsza niż początek tury.
    /// </summary>
    public static long? Find(IReadOnlyList<string> lines, long turnStartedAt)
    {
        // Lokalne polecenie (/config, !git status) wpisane po przerwaniu to nie nowa tura: zostawia
        // wpis polecenia i zaraz po nim jego wynik. Polecenie skillu wyniku lokalnego nie ma i rusza turę.
        var afterLocalOutput = false;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) continue;
                var kind = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
                if (kind is not ("user" or "assistant")) continue;
                if (IsTrue(root, "isSidechain") || IsTrue(root, "isMeta")) continue;
                if (kind == "user")
                {
                    var text = FirstText(root);
                    if (text.StartsWith("<local-command-", StringComparison.Ordinal) || text.StartsWith("<bash-", StringComparison.Ordinal))
                    {
                        afterLocalOutput = true;
                        continue;
                    }
                    if (afterLocalOutput && (text.StartsWith("<command-name>", StringComparison.Ordinal) || text.StartsWith("<command-message>", StringComparison.Ordinal)))
                    {
                        continue;
                    }
                }
                if (kind == "assistant" || !IsInterruptMessage(root)) return null;
                return Timestamp(root) is long at && at >= turnStartedAt ? at : null;
            }
            catch (JsonException)
            {
                // Urwana linia na początku odczytanego fragmentu albo coś nowego w formacie.
            }
        }
        return null;
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsInterruptMessage(JsonElement root) =>
        Texts(root).Any(text => text.StartsWith(Marker, StringComparison.Ordinal));

    private static string FirstText(JsonElement root) => Texts(root).FirstOrDefault()?.TrimStart() ?? "";

    // Tekst wiadomości: cała treść albo kolejne części „text” (wyniki narzędzi się pomija).
    private static IEnumerable<string> Texts(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) yield break;
        if (!message.TryGetProperty("content", out var content)) yield break;
        if (content.ValueKind == JsonValueKind.String)
        {
            yield return content.GetString()!;
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var partType)
                && partType.ValueKind == JsonValueKind.String && partType.GetString() == "text"
                && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                yield return text.GetString()!;
            }
        }
    }

    private static long? Timestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;

    private static List<string> ReadTail(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - start];
        stream.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // Pierwsza linia fragmentu jest zwykle urwana w połowie.
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
        return lines;
    }
}
