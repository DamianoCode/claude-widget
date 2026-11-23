using System.Globalization;
using System.Text.Json;

namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// Kontekst odczytany z transkryptu. <see cref="Tokens"/> null — rozmowę właśnie streszczono
/// (compact) i nowego rozmiaru jeszcze nie znamy. <see cref="At"/> — kiedy powstał ten wpis.
/// </summary>
public sealed record TranscriptReading(double? Tokens, string? Model, long? At);

/// <summary>
/// Kontekst sesji z jej transkryptu — dla sesji bez statusline (Orca, Agent SDK, IDE, sesje w tle)
/// i wtedy, gdy statusline sesji ucichła. Ostatnia odpowiedź głównej rozmowy ma <c>usage</c>;
/// wejście (z cache) to dokładnie to, co mieści teraz kontekst — z tej samej sumy Claude Code
/// liczy procent w statusline.
///
/// Działa w odświeżaniu widoku co sekundę, więc czyta oszczędnie: od końca, kawałkami, dekodując
/// tylko linie odpowiedzi i streszczeń; po dopisaniu do pliku — tylko to, co doszło.
/// Transkrypt to format wewnętrzny Claude Code: czego nie da się odczytać, tego nie pokazujemy.
/// </summary>
public sealed class TranscriptContext
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Tyle końcówki przeglądamy najwyżej przy pierwszym odczycie albo po podmianie pliku.</summary>
    private const long MaxScanBytes = 8L * 1024 * 1024;

    /// <summary>Dłuższe linie to wyniki narzędzi — odpowiedź z <c>usage</c> nigdy nie jest tak duża.</summary>
    private const int MaxLineBytes = 2 * 1024 * 1024;

    private static readonly byte[] AssistantMarker = "\"type\":\"assistant\""u8.ToArray();
    private static readonly byte[] CompactMarker = "\"compact_boundary\""u8.ToArray();

    private sealed record Entry(long Length, DateTime WrittenUtc, long CompleteEnd, TranscriptReading? Result);

    private readonly Dictionary<string, Entry> _cache = [];

    /// <summary>Ostatni rozstrzygający wpis transkryptu albo null (brak pliku, brak odpowiedzi).</summary>
    public TranscriptReading? Read(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath) || !transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var file = new FileInfo(transcriptPath);
            if (!file.Exists) return null;
            _cache.TryGetValue(transcriptPath, out var cached);
            if (cached is not null && cached.Length == file.Length && cached.WrittenUtc == file.LastWriteTimeUtc) return cached.Result;

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            // Ostatnia linia bez końca wiersza może być jeszcze dopisywana — wróci przy następnym odczycie.
            var completeEnd = LastLineEnd(stream, length);
            TranscriptReading? result;
            if (cached is not null && length >= cached.Length && completeEnd >= cached.CompleteEnd)
            {
                // Plik tylko urósł: wystarczy przejrzeć dopisane linie.
                result = Scan(stream, cached.CompleteEnd, completeEnd, fromIsLineStart: true) ?? cached.Result;
            }
            else
            {
                var from = Math.Max(0, completeEnd - MaxScanBytes);
                result = Scan(stream, from, completeEnd, fromIsLineStart: from == 0);
            }
            _cache[transcriptPath] = new Entry(length, file.LastWriteTimeUtc, completeEnd, result);
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

    /// <summary>Ostatni rozstrzygający wpis spośród linii (od końca) albo null, gdy żadnego nie ma.</summary>
    public static TranscriptReading? Find(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (Parse(System.Text.Encoding.UTF8.GetBytes(lines[i])) is { } reading) return reading;
        }
        return null;
    }

    /// <summary>
    /// Wpis, który rozstrzyga o kontekście: odpowiedź głównej rozmowy z <c>usage</c> albo streszczenie
    /// rozmowy. Podagenci, wiadomości wstawiane przez samo Claude Code i inne wpisy — null.
    /// </summary>
    public static TranscriptReading? Parse(ReadOnlySpan<byte> line)
    {
        var assistant = line.IndexOf(AssistantMarker) >= 0;
        if (!assistant && line.IndexOf(CompactMarker) < 0) return null;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return null;
            var type = Text(root, "type");
            var at = Timestamp(root);
            if (type == "system" && Text(root, "subtype") == "compact_boundary") return new TranscriptReading(null, null, at);
            if (type != "assistant") return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
            var model = Text(message, "model");
            if (model == "<synthetic>") return null;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
            var tokens = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
            return tokens > 0 ? new TranscriptReading(tokens, model, at) : null;
        }
        catch (JsonException)
        {
            // Urwana linia albo coś nowego w formacie.
            return null;
        }
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

    /// <summary>Pozycja tuż za ostatnim znakiem końca wiersza (0, gdy go nie ma w zasięgu).</summary>
    private static long LastLineEnd(FileStream stream, long length)
    {
        var buffer = new byte[ChunkBytes];
        var pos = length;
        var floor = Math.Max(0, length - MaxScanBytes);
        while (pos > floor)
        {
            var size = (int)Math.Min(ChunkBytes, pos - floor);
            pos -= size;
            stream.Position = pos;
            stream.ReadExactly(buffer, 0, size);
            var newline = buffer.AsSpan(0, size).LastIndexOf((byte)'\n');
            if (newline >= 0) return pos + newline + 1;
        }
        return floor;
    }

    /// <summary>
    /// Przegląda pełne linie w [<paramref name="from"/>, <paramref name="end"/>) od końca i zwraca
    /// pierwszy rozstrzygający wpis. Gdy <paramref name="from"/> nie jest początkiem linii, pierwszy
    /// (urwany) kawałek się pomija.
    /// </summary>
    private static TranscriptReading? Scan(FileStream stream, long from, long end, bool fromIsLineStart)
    {
        // Linia rośnie od prawej do lewej: fragmenty z kolejnych (wcześniejszych) kawałków pliku.
        var parts = new List<byte[]>();
        var lineBytes = 0L;
        var buffer = new byte[ChunkBytes];
        var pos = end;
        while (pos > from)
        {
            var size = (int)Math.Min(ChunkBytes, pos - from);
            pos -= size;
            stream.Position = pos;
            stream.ReadExactly(buffer, 0, size);
            var segmentEnd = size;
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (Complete(buffer.AsSpan(i + 1, segmentEnd - i - 1), parts, ref lineBytes) is { } found) return found;
                segmentEnd = i;
            }
            Prepend(buffer.AsSpan(0, segmentEnd), parts, ref lineBytes);
        }
        return fromIsLineStart ? Complete([], parts, ref lineBytes) : null;
    }

    private static void Prepend(ReadOnlySpan<byte> segment, List<byte[]> parts, ref long lineBytes)
    {
        lineBytes += segment.Length;
        // Za długiej linii nie składamy — i tak ją pominiemy; liczymy tylko jej długość.
        if (lineBytes <= MaxLineBytes && segment.Length > 0) parts.Insert(0, segment.ToArray());
        else if (lineBytes > MaxLineBytes) parts.Clear();
    }

    private static TranscriptReading? Complete(ReadOnlySpan<byte> head, List<byte[]> parts, ref long lineBytes)
    {
        var total = lineBytes + head.Length;
        TranscriptReading? result = null;
        if (total > 0 && total <= MaxLineBytes)
        {
            if (parts.Count == 0)
            {
                result = Parse(head);
            }
            else
            {
                var line = new byte[total];
                head.CopyTo(line);
                var offset = head.Length;
                foreach (var part in parts)
                {
                    part.CopyTo(line, offset);
                    offset += part.Length;
                }
                result = Parse(line);
            }
        }
        parts.Clear();
        lineBytes = 0;
        return result;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Max(0, number)
            : 0;

    private static long? Timestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;
}
