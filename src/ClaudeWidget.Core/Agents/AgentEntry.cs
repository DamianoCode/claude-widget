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
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<AgentEntry>))]
public sealed partial class AgentsJson : JsonSerializerContext;
