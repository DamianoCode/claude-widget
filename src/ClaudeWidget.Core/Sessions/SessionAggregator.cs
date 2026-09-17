using System.Text.RegularExpressions;
using ClaudeWidget.Core.Agents;

namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// Czyta pliki stanu z katalogu widżetu, sprząta po sesjach, które zamknęły się bez SessionEnd,
/// i dokłada sesje z listy agentów. Port <c>Get-Sessions</c>/<c>Merge-AgentSessions</c>/
/// <c>Remove-ClosedSession</c>/<c>Remove-OrphanFiles</c> z widget.ps1.
/// </summary>
public sealed partial class SessionAggregator(WidgetPaths paths, IProcessProbe probe)
{
    private readonly InterruptedTurn _interrupted = new();

    // Sesja bez PID (sprzed wersji hooka z PID) i bez zdarzeń od 12 h najpewniej już nie istnieje.
    private const long StaleMs = 12 * 3_600_000L;
    private const long AgentsStaleMs = 60_000L;

    /// <summary>PID sesji, której pliki właśnie usunięto — wywołujący może zapomnieć cache okna hosta.</summary>
    public event Action<int>? SessionClosed;

    /// <summary>
    /// Sesje gotowe do pokazania: pilniejsze pierwsze, przy remisie ta, która zmieniła stan
    /// najpóźniej. Przy okazji usuwa pliki sesji, których proces już nie żyje.
    /// </summary>
    public IReadOnlyList<SessionInfo> GetSessions(long nowMs, AgentsSnapshot? agents = null)
    {
        var sessions = new List<SessionInfo>();
        if (!Directory.Exists(paths.StateDir)) return sessions;
        var transcripts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(paths.StateDir, "*.state.json"))
        {
            var id = Path.GetFileName(file)[..^".state.json".Length];
            var state = JsonStore.Read(file, StateJson.Default.SessionState);
            if (state is null || !SessionStates.IsKnown(state.State)) continue;

            if (state.Pid is int pid)
            {
                if (!IsSessionProcessAlive(pid, state.UpdatedAt))
                {
                    RemoveClosedSession(id, state);
                    continue;
                }
            }
            else if (nowMs - state.UpdatedAt > StaleMs)
            {
                RemoveClosedSession(id, state);
                continue;
            }

            var usage = JsonStore.Read(paths.SessionFile(id, SessionFileKind.Usage)!, StateJson.Default.SessionUsage);
            var seen = JsonStore.Read(paths.SessionFile(id, SessionFileKind.Seen)!, StateJson.Default.SeenMarker);
            var seenAt = seen?.SeenAt ?? 0;
            var since = state.Since;
            var kind = state.State == SessionStates.Done
                ? state.Fresh == true && seenAt < since ? SessionKinds.New : SessionKinds.Idle
                : state.State;
            var detail = state.Detail ?? "";
            // Tura przerwana Esc — w trakcie pracy albo odmową w oknie zgody: sesja stoi, a nowego
            // wyniku nie ma, bo przerwałeś ją sam. Czerwone z błędu API (bez narzędzia) zostaje.
            var working = state.State == SessionStates.Working && state.Background is null or 0;
            var askingPermission = state.State == SessionStates.Waiting && state.PendingTool.HasValue;
            if ((working || askingPermission) && state.TranscriptPath is { } transcript)
            {
                transcripts.Add(transcript);
                var turnStart = working ? state.TurnStartedAt ?? since : since;
                if (_interrupted.Check(transcript, turnStart) is long interruptedAt)
                {
                    kind = SessionKinds.Idle;
                    since = interruptedAt;
                    detail = "";
                }
            }
            var project = ProjectOf(usage?.Project, state.Cwd);

            sessions.Add(new SessionInfo
            {
                Id = id,
                Kind = kind,
                Since = since,
                Detail = detail,
                Summary = state.Summary ?? "",
                Background = state.Background ?? 0,
                Pid = state.Pid ?? 0,
                Project = project,
                Cwd = state.Cwd ?? "",
                Name = !string.IsNullOrEmpty(usage?.Name) ? usage.Name : project,
                ContextPct = usage?.ContextPct,
                ContextTokens = usage?.ContextTokens,
                ContextSize = usage?.ContextSize,
            });
        }

        _interrupted.Retain(transcripts);
        MergeAgentSessions(sessions, agents, nowMs);

        return sessions
            .OrderByDescending(session => SessionKindCatalog.Kinds[session.Kind].Rank)
            .ThenByDescending(session => session.Since)
            .ToList();
    }

    /// <summary>
    /// Dokłada sesje z listy `claude agents --json`: sesje w tle, których hooki widżet pomija,
    /// i sesje w terminalu, które nie wysłały jeszcze żadnego zdarzenia. Nieaktualna lista
    /// (np. brak claude w PATH) jest pomijana — widżet działa wtedy na samych hookach.
    /// </summary>
    private void MergeAgentSessions(List<SessionInfo> sessions, AgentsSnapshot? agents, long nowMs)
    {
        if (!IsUsable(agents, nowMs)) return;

        var known = new Dictionary<string, SessionInfo>();
        foreach (var session in sessions) known[session.Id] = session;

        foreach (var entry in agents.Sessions)
        {
            var id = entry.SessionId;
            if (string.IsNullOrEmpty(id)) continue;
            var project = ProjectOf(null, entry.Cwd);

            if (entry.Kind == "background")
            {
                // Po /bg rozmową zarządza proces nadzorczy, a hooki tej sesji są już pomijane,
                // więc jej dawny stan z hooka jest nieaktualny.
                if (known.TryGetValue(id, out var stale)) sessions.Remove(stale);
                var seen = JsonStore.Read(paths.SessionFile(id, SessionFileKind.Seen)!, StateJson.Default.SeenMarker);
                var seenAt = seen?.SeenAt ?? 0;
                var kind = entry.State switch
                {
                    "blocked" => SessionKinds.Waiting,
                    "failed" => SessionKinds.Waiting,
                    "done" => entry.Fresh && seenAt < entry.Since ? SessionKinds.New : SessionKinds.Idle,
                    _ => SessionKinds.Working,
                };
                var detail = entry.State switch
                {
                    "blocked" => !string.IsNullOrEmpty(entry.WaitingFor) ? entry.WaitingFor : "Czeka na Twoją odpowiedź",
                    "failed" => "Zakończona błędem",
                    _ => "",
                };
                sessions.Add(new SessionInfo
                {
                    Id = id,
                    Kind = kind,
                    Since = entry.Since,
                    Detail = detail,
                    Summary = "",
                    Background = 0,
                    Pid = 0,
                    Project = project,
                    Cwd = entry.Cwd ?? "",
                    Name = !string.IsNullOrEmpty(entry.Name) ? entry.Name : entry.Id,
                    IsBackground = true,
                    ShortId = entry.Id,
                });
            }
            else if (!known.ContainsKey(id) && entry.Pid is int entryPid && probe.Get(entryPid) is not null)
            {
                // Sesja w terminalu bez zdarzeń z hooka, np. otwarta przed instalacją widżetu.
                var usage = JsonStore.Read(paths.SessionFile(id, SessionFileKind.Usage)!, StateJson.Default.SessionUsage);
                if (!string.IsNullOrEmpty(usage?.Project)) project = usage.Project;
                sessions.Add(new SessionInfo
                {
                    Id = id,
                    Kind = entry.Status == "busy" ? SessionKinds.Working : SessionKinds.Idle,
                    Since = entry.StartedAt ?? nowMs,
                    Background = 0,
                    Pid = entryPid,
                    Project = project,
                    Cwd = entry.Cwd ?? "",
                    Name = !string.IsNullOrEmpty(usage?.Name) ? usage.Name : project,
                    ContextPct = usage?.ContextPct,
                    ContextTokens = usage?.ContextTokens,
                    ContextSize = usage?.ContextSize,
                });
            }
        }
    }

    /// <summary>Lista agentów udana i świeża — bez niej sesji w tle po prostu nie widać.</summary>
    public static bool IsUsable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] AgentsSnapshot? agents, long nowMs) =>
        agents is { Ok: true } && nowMs - agents.UpdatedAt <= AgentsStaleMs;

    // Po zamknięciu sesji jej PID może dostać inny proces. Nazwa odsiewa inne programy, a czas
    // startu — inny proces claude albo node: ten, który ruszył po ostatnim zapisie hooka, nie jest
    // tą sesją.
    private bool IsSessionProcessAlive(int pid, long updatedAt)
    {
        var snapshot = probe.Get(pid);
        if (snapshot is null || !ClaudeOrNodeProcessName().IsMatch(snapshot.Name)) return false;
        return snapshot.StartTimeMs is not long startMs || startMs <= updatedAt;
    }

    /// <summary>
    /// Usuwa stan i dane statusline sesji zamkniętej bez SessionEnd (zamknięte okno, awaria).
    /// Znacznik przejrzenia zostaje, bo sesja mogła przejść do tła; stare znaczniki zbiera
    /// <see cref="CleanupOrphans"/>.
    /// </summary>
    private void RemoveClosedSession(string id, SessionState state)
    {
        // Wznowiona sesja (ten sam id, nowy proces) mogła właśnie zapisać swój stan — ten zostaje.
        var currentPath = paths.SessionFile(id, SessionFileKind.State)!;
        var current = JsonStore.Read(currentPath, StateJson.Default.SessionState);
        if (current is not null && current.Pid != state.Pid) return;

        JsonStore.Remove(currentPath);
        JsonStore.Remove(paths.SessionFile(id, SessionFileKind.Usage));
        if (state.Pid is int pid) SessionClosed?.Invoke(pid);
    }

    /// <summary>
    /// Pliki bez sesji: znaczniki przejrzenia i dane statusline po sesjach, których stanu już nie
    /// ma, oraz pliki tymczasowe po przerwanym zapisie. Doba zapasu, bo skończona sesja w tle
    /// zostaje na liście 12 h i do tego czasu jej znacznik przejrzenia jest potrzebny.
    /// </summary>
    public void CleanupOrphans(DateTime? nowUtc = null)
    {
        if (!Directory.Exists(paths.StateDir)) return;
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddHours(-24);
        foreach (var file in Directory.EnumerateFiles(paths.StateDir))
        {
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTimeUtc(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (lastWrite > cutoff) continue;

            var name = Path.GetFileName(file);
            var match = OrphanMarkerFile().Match(name);
            var orphan = match.Success
                ? !File.Exists(Path.Combine(paths.StateDir, $"{match.Groups[1].Value}.state.json"))
                : name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            if (orphan) JsonStore.Remove(file);
        }
    }

    private static string ProjectOf(string? usageProject, string? cwd)
    {
        if (!string.IsNullOrEmpty(usageProject)) return usageProject;
        if (!string.IsNullOrEmpty(cwd))
        {
            var trimmed = cwd.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return "sesja";
    }

    [GeneratedRegex("^(claude|node)", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeOrNodeProcessName();

    [GeneratedRegex(@"^(.+)\.(seen|usage)\.json$")]
    private static partial Regex OrphanMarkerFile();
}
