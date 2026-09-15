using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWidget.Core.Agents;

/// <summary>Jeden wpis z `claude agents --json --all`, tak jak go drukuje Claude Code.</summary>
public sealed record AgentEntry
{
    public string? SessionId { get; init; }

    public string? Id { get; init; }

    public string? Kind { get; init; }

    public int? Pid { get; init; }

    public string? Cwd { get; init; }

    public string? Name { get; init; }

    public string? Status { get; init; }

    public double? StartedAt { get; init; }

    public string? State { get; init; }

    /// <summary>Napis albo obiekt — patrz <c>AgentsTracker.DescribeWaiting</c>.</summary>
    public JsonElement? WaitingFor { get; init; }

    /// <summary>
    /// Cała odpowiedź `claude agents --json --all`: każdy element wczytuje się osobno i wyrozumiale,
    /// więc jeden zniekształcony wpis nie wywala reszty listy — port `for (const entry of listed)`
    /// z agents.mjs. Rzuca, gdy JSON jest niepoprawny albo korzeń nie jest tablicą.
    /// </summary>
    public static IReadOnlyList<AgentEntry> ParseListing(string json)
    {
        var root = JsonSerializer.Deserialize(json, AgentsJson.Default.JsonElement);
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("claude agents --json nie zwrócił listy sesji");
        }
        var entries = new List<AgentEntry>();
        foreach (var element in root.EnumerateArray())
        {
            var entry = Parse(element);
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Wyrozumiałe wczytanie jednego elementu tablicy — port sposobu, w jaki JS-owy <c>track()</c>
    /// czyta pola przez proste rzutowanie (typeof/Number.isInteger/Number.isFinite), nie przez
    /// ścisły parser. Jeden zniekształcony wpis (np. z przyszłej wersji CLI) nie wywraca reszty
    /// listy. Zwraca null, gdy element nie jest obiektem albo brakuje mu poprawnego sessionId —
    /// dokładnie tak, jak JS-owe `if (!entry || typeof entry.sessionId !== 'string') continue;`.
    /// </summary>
    public static AgentEntry? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!TryGetString(element, "sessionId", out var sessionId) || sessionId is null) return null;

        return new AgentEntry
        {
            SessionId = sessionId,
            Id = TryGetString(element, "id", out var id) ? id : null,
            Kind = TryGetString(element, "kind", out var kind) ? kind : null,
            Pid = TryGetInteger(element, "pid"),
            Cwd = TryGetString(element, "cwd", out var cwd) ? cwd : "",
            Name = TryGetString(element, "name", out var name) ? name : "",
            Status = TryGetString(element, "status", out var status) ? status : "",
            StartedAt = TryGetFiniteNumber(element, "startedAt"),
            State = TryGetStateString(element),
            WaitingFor = element.TryGetProperty("waitingFor", out var waitingFor) ? waitingFor : null,
        };
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        if (element.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.String)
        {
            value = candidate.GetString();
            return true;
        }
        value = null;
        return false;
    }

    // Number.isInteger(entry.pid) ? entry.pid : null — inne typy, ułamki, NaN itd. dają null.
    private static int? TryGetInteger(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.Number
            && candidate.TryGetInt32(out var value))
        {
            return value;
        }
        return null;
    }

    // Number.isFinite(entry.startedAt) ? entry.startedAt : null
    private static double? TryGetFiniteNumber(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.Number
            && candidate.TryGetDouble(out var value) && double.IsFinite(value))
        {
            return value;
        }
        return null;
    }

    // background ? String(entry.state ?? 'working') : null — entry.state bywa nie-napisem
    // (np. liczbą z przyszłej wersji CLI), JS wtedy po prostu go stringifikuje.
    private static string? TryGetStateString(JsonElement element)
    {
        if (!element.TryGetProperty("state", out var candidate) || candidate.ValueKind == JsonValueKind.Null) return null;
        return candidate.ValueKind switch
        {
            JsonValueKind.String => candidate.GetString(),
            JsonValueKind.Number => candidate.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class AgentsJson : JsonSerializerContext;
