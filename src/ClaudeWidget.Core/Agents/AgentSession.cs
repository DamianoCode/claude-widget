namespace ClaudeWidget.Core.Agents;

/// <summary>Sesja z listy `claude agents --json --all`, wzbogacona o to, od kiedy jest w danym stanie.</summary>
public sealed record AgentSession
{
    public required string SessionId { get; init; }

    public required string Id { get; init; }

    /// <summary>"background" albo "interactive".</summary>
    public required string Kind { get; init; }

    public int? Pid { get; init; }

    public string Cwd { get; init; } = "";

    public string Name { get; init; } = "";

    public string Status { get; init; } = "";

    public long? StartedAt { get; init; }

    /// <summary>Tylko sesje w tle: "working", "blocked", "failed", "done"; null dla interaktywnych.</summary>
    public string? State { get; init; }

    public string WaitingFor { get; init; } = "";

    public long Since { get; init; }

    /// <summary>Przy stanie "done": true, gdy widżet widział, jak sesja kończyła pracę, i jeszcze go nie przejrzał.</summary>
    public bool Fresh { get; init; }
}

/// <summary>Wynik ostatniego przebiegu `claude agents --json --all` (odpowiednik agents.json z wersji Node).</summary>
public sealed record AgentsSnapshot(bool Ok, long UpdatedAt, IReadOnlyList<AgentSession> Sessions);
