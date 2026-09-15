using ClaudeWidget.Core;
using ClaudeWidget.Core.Agents;
using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget.Tests.Sessions;

/// <summary>A fake process table the tests can shape freely: alive/dead pid, name, start time.</summary>
public sealed class FakeProcessProbe : IProcessProbe
{
    private readonly Dictionary<int, ProcessSnapshot> _processes = new();

    public FakeProcessProbe Alive(int pid, string name = "claude", long? startMs = 0)
    {
        _processes[pid] = new ProcessSnapshot(name, startMs);
        return this;
    }

    public ProcessSnapshot? Get(int pid) => _processes.GetValueOrDefault(pid);
}

public sealed class SessionAggregatorTests
{
    private static void WriteState(WidgetPaths paths, string id, SessionState state) =>
        JsonStore.Write(paths.SessionFile(id, SessionFileKind.State)!, state, StateJson.Default.SessionState);

    private static void WriteUsage(WidgetPaths paths, string id, SessionUsage usage) =>
        JsonStore.Write(paths.SessionFile(id, SessionFileKind.Usage)!, usage, StateJson.Default.SessionUsage);

    private static void WriteSeen(WidgetPaths paths, string id, long seenAt) =>
        JsonStore.Write(paths.SessionFile(id, SessionFileKind.Seen)!, new SeenMarker { SeenAt = seenAt }, StateJson.Default.SeenMarker);

    [Fact]
    public void Waiting_session_with_alive_process_is_kept_with_its_kind()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Waiting, Since = 1000, Pid = 111, UpdatedAt = 1000 });
        var probe = new FakeProcessProbe().Alive(111);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 2000);

        Assert.Single(sessions);
        Assert.Equal(SessionKinds.Waiting, sessions[0].Kind);
    }

    [Fact]
    public void Done_session_not_yet_seen_is_a_new_result()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Done, Since = 5000, Fresh = true, Pid = 111, UpdatedAt = 5000 });
        var probe = new FakeProcessProbe().Alive(111);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 6000);

        Assert.Equal(SessionKinds.New, sessions[0].Kind);
    }

    [Fact]
    public void Done_session_seen_after_it_finished_is_idle()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Done, Since = 5000, Fresh = true, Pid = 111, UpdatedAt = 5000 });
        WriteSeen(paths, "s1", seenAt: 5500);
        var probe = new FakeProcessProbe().Alive(111);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 6000);

        Assert.Equal(SessionKinds.Idle, sessions[0].Kind);
    }

    [Fact]
    public void Dead_pid_closes_the_session_and_deletes_its_files()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 999, UpdatedAt = 1000 });
        WriteUsage(paths, "s1", new SessionUsage { Project = "demo", UpdatedAt = 1000 });
        var probe = new FakeProcessProbe(); // pid 999 not registered => dead

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 2000);

        Assert.Empty(sessions);
        Assert.False(File.Exists(paths.SessionFile("s1", SessionFileKind.State)));
        Assert.False(File.Exists(paths.SessionFile("s1", SessionFileKind.Usage)));
    }

    [Fact]
    public void Process_alive_but_not_claude_or_node_closes_the_session()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 555, UpdatedAt = 1000 });
        var probe = new FakeProcessProbe().Alive(555, name: "notepad");

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 2000);

        Assert.Empty(sessions);
    }

    [Fact]
    public void Pid_reused_by_a_process_started_after_the_last_hook_write_closes_the_session()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 777, UpdatedAt = 1000 });
        // The new process (whatever reused the pid) started after the hook's last write.
        var probe = new FakeProcessProbe().Alive(777, name: "claude", startMs: 5000);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 6000);

        Assert.Empty(sessions);
        Assert.False(File.Exists(paths.SessionFile("s1", SessionFileKind.State)));
    }

    [Fact]
    public void Process_started_before_the_last_hook_write_is_the_same_session()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 777, UpdatedAt = 5000 });
        var probe = new FakeProcessProbe().Alive(777, name: "claude", startMs: 1000);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 6000);

        Assert.Single(sessions);
    }

    /// <summary>
    /// A process probe whose answer for one PID has the side effect of rewriting the session file,
    /// so the test can land exactly in the race window between the aggregator's first read and its
    /// re-read-before-delete: the old process is confirmed dead, but the session already resumed
    /// under a new pid by the time the file would be removed.
    /// </summary>
    private sealed class ResumingProbe(WidgetPaths paths, int deadPid) : IProcessProbe
    {
        public ProcessSnapshot? Get(int pid)
        {
            if (pid != deadPid) return null;
            WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 9000, Pid = 222, UpdatedAt = 9000 });
            return null; // the original process is genuinely gone
        }
    }

    [Fact]
    public void Resumed_session_with_a_new_pid_is_kept_not_deleted()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 111, UpdatedAt = 1000 });

        var sessions = new SessionAggregator(paths, new ResumingProbe(paths, deadPid: 111)).GetSessions(nowMs: 9500);

        // The old snapshot (pid 111) is dropped from this pass, but the file on disk — already
        // rewritten for the resumed pid 222 — must not be deleted.
        Assert.Empty(sessions);
        Assert.True(File.Exists(paths.SessionFile("s1", SessionFileKind.State)));
    }

    [Fact]
    public void Legacy_session_without_pid_is_stale_after_twelve_hours()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 0, UpdatedAt = 0 });
        var probe = new FakeProcessProbe();

        var stillThere = new SessionAggregator(paths, probe).GetSessions(nowMs: 11 * 3_600_000L);
        Assert.Single(stillThere);

        var gone = new SessionAggregator(paths, probe).GetSessions(nowMs: 13 * 3_600_000L);
        Assert.Empty(gone);
        Assert.False(File.Exists(paths.SessionFile("s1", SessionFileKind.State)));
    }

    [Fact]
    public void Seen_marker_survives_session_closing()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Done, Since = 1000, Pid = 999, UpdatedAt = 1000 });
        WriteSeen(paths, "s1", seenAt: 1500);
        var probe = new FakeProcessProbe(); // dead

        new SessionAggregator(paths, probe).GetSessions(nowMs: 2000);

        Assert.True(File.Exists(paths.SessionFile("s1", SessionFileKind.Seen)));
    }

    [Fact]
    public void Session_closed_event_reports_the_freed_pid()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 999, UpdatedAt = 1000 });
        var probe = new FakeProcessProbe();
        var aggregator = new SessionAggregator(paths, probe);
        int? closedPid = null;
        aggregator.SessionClosed += pid => closedPid = pid;

        aggregator.GetSessions(nowMs: 2000);

        Assert.Equal(999, closedPid);
    }

    [Fact]
    public void Orphan_sweep_removes_old_seen_and_usage_files_without_a_matching_state_file()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        Directory.CreateDirectory(paths.StateDir);
        WriteSeen(paths, "gone", seenAt: 1);
        WriteUsage(paths, "gone", new SessionUsage { UpdatedAt = 1 });
        File.WriteAllText(Path.Combine(paths.StateDir, "leftover.tmp"), "x");
        var old = DateTime.UtcNow.AddHours(-25);
        foreach (var file in Directory.EnumerateFiles(paths.StateDir)) File.SetLastWriteTimeUtc(file, old);

        new SessionAggregator(paths, new FakeProcessProbe()).CleanupOrphans();

        Assert.Empty(Directory.EnumerateFiles(paths.StateDir));
    }

    [Fact]
    public void Orphan_sweep_keeps_recent_files_and_files_with_a_matching_session()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "s1", new SessionState { State = SessionStates.Working, Since = 1000, UpdatedAt = 1000 });
        WriteUsage(paths, "s1", new SessionUsage { UpdatedAt = 1000 }); // has a matching state file
        WriteSeen(paths, "recent-orphan", seenAt: 1); // orphan but written just now
        var old = DateTime.UtcNow.AddHours(-25);
        File.SetLastWriteTimeUtc(paths.SessionFile("s1", SessionFileKind.State)!, old);
        File.SetLastWriteTimeUtc(paths.SessionFile("s1", SessionFileKind.Usage)!, old);

        new SessionAggregator(paths, new FakeProcessProbe()).CleanupOrphans();

        Assert.True(File.Exists(paths.SessionFile("s1", SessionFileKind.State)));
        Assert.True(File.Exists(paths.SessionFile("s1", SessionFileKind.Usage)));
        Assert.True(File.Exists(paths.SessionFile("recent-orphan", SessionFileKind.Seen)));
    }

    [Fact]
    public void Merge_replaces_hook_state_with_background_listing_for_the_same_session()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "349831c7", new SessionState { State = SessionStates.Working, Since = 1000, UpdatedAt = 1000 });
        var agents = new AgentsSnapshot(true, 2000, [
            new AgentSession { SessionId = "349831c7", Id = "349831c7", Kind = "background", State = "blocked", WaitingFor = "Czeka na Twoją zgodę", Since = 1500 },
        ]);

        var sessions = new SessionAggregator(paths, new FakeProcessProbe()).GetSessions(nowMs: 2000, agents);

        Assert.Single(sessions);
        Assert.Equal(SessionKinds.Waiting, sessions[0].Kind);
        Assert.True(sessions[0].IsBackground);
    }

    [Fact]
    public void Merge_adds_an_interactive_session_only_when_its_pid_is_alive()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        var probe = new FakeProcessProbe().Alive(4242);
        var agents = new AgentsSnapshot(true, 2000, [
            new AgentSession { SessionId = "term-1", Id = "term-1", Kind = "interactive", Pid = 4242, Status = "busy", Cwd = "C:\\apps\\x" },
            new AgentSession { SessionId = "term-2", Id = "term-2", Kind = "interactive", Pid = 9999, Status = "busy", Cwd = "C:\\apps\\y" },
        ]);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 2000, agents);

        Assert.Single(sessions);
        Assert.Equal("term-1", sessions[0].Id);
        Assert.Equal(SessionKinds.Working, sessions[0].Kind);
    }

    [Fact]
    public void Stale_agents_listing_is_ignored()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        var agents = new AgentsSnapshot(true, 0, [
            new AgentSession { SessionId = "bg", Id = "bg", Kind = "background", State = "working", Since = 0 },
        ]);

        var sessions = new SessionAggregator(paths, new FakeProcessProbe()).GetSessions(nowMs: 70_000, agents);

        Assert.Empty(sessions);
    }

    [Fact]
    public void Failed_agents_listing_is_ignored()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        var agents = new AgentsSnapshot(false, 2000, [
            new AgentSession { SessionId = "bg", Id = "bg", Kind = "background", State = "working", Since = 2000 },
        ]);

        var sessions = new SessionAggregator(paths, new FakeProcessProbe()).GetSessions(nowMs: 2000, agents);

        Assert.Empty(sessions);
    }

    [Fact]
    public void Sessions_are_sorted_by_urgency_then_by_most_recent_change()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        WriteState(paths, "idle", new SessionState { State = SessionStates.Done, Since = 1000, Fresh = false, Pid = 1, UpdatedAt = 1000 });
        WriteState(paths, "working", new SessionState { State = SessionStates.Working, Since = 2000, Pid = 2, UpdatedAt = 2000 });
        WriteState(paths, "waiting-old", new SessionState { State = SessionStates.Waiting, Since = 1500, Pid = 3, UpdatedAt = 1500 });
        WriteState(paths, "waiting-new", new SessionState { State = SessionStates.Waiting, Since = 3000, Pid = 4, UpdatedAt = 3000 });
        var probe = new FakeProcessProbe().Alive(1).Alive(2).Alive(3).Alive(4);

        var sessions = new SessionAggregator(paths, probe).GetSessions(nowMs: 4000);

        Assert.Equal(["waiting-new", "waiting-old", "working", "idle"], sessions.Select(s => s.Id));
    }
}
