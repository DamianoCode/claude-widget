namespace ClaudeWidget.Core.Sessions;

/// <summary>Jedna sesja widoczna w widżecie — z hooka/statusline albo z listy agentów.</summary>
public sealed record SessionInfo
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public long Since { get; init; }

    public string Detail { get; init; } = "";

    /// <summary>Pierwsze zdanie ostatniej odpowiedzi — podgląd nowego wyniku.</summary>
    public string Summary { get; init; } = "";

    public int Background { get; init; }

    /// <summary>PID procesu Claude Code; 0, gdy sesja pochodzi wyłącznie z listy agentów w tle.</summary>
    public int Pid { get; init; }

    public required string Project { get; init; }

    /// <summary>Katalog roboczy sesji; pusty, gdy nieznany. Po nim szuka się okna IDE z tym workspace.</summary>
    public string Cwd { get; init; } = "";

    public required string Name { get; init; }

    public double? ContextPct { get; init; }

    public double? ContextTokens { get; init; }

    public double? ContextSize { get; init; }

    /// <summary>Sesja w tle (`/bg`, `claude --bg`) — nie ma okna, otwiera się przez `claude attach`.</summary>
    public bool IsBackground { get; init; }

    /// <summary>Krótkie id do `claude attach <id>`; puste dla sesji, które nie są w tle.</summary>
    public string ShortId { get; init; } = "";
}
