using System.Text.Json;
using ClaudeWidget.Core.Agents;

namespace ClaudeWidget.Tests.Agents;

/// <summary>Port of tests/agents.test.mjs — one case per test in the Node suite, same fixtures.</summary>
public sealed class AgentsTrackerTests
{
    private const long Hour = 3_600_000L;

    private static AgentEntry Interactive(Action<Dictionary<string, object?>>? edit = null) =>
        Build(new Dictionary<string, object?>
        {
            ["pid"] = 25296, ["cwd"] = "C:\\apps\\claudius", ["kind"] = "interactive", ["startedAt"] = 1000d,
            ["sessionId"] = "5a5cb1ac-2aa8-460a-a0af-b8e980134376", ["name"] = "claudius-1c", ["status"] = "busy",
        }, edit);

    private static AgentEntry Background(Action<Dictionary<string, object?>>? edit = null) =>
        Build(new Dictionary<string, object?>
        {
            ["pid"] = 22808, ["id"] = "349831c7", ["cwd"] = "C:\\apps\\api", ["kind"] = "background", ["startedAt"] = 2000d,
            ["sessionId"] = "349831c7-8a94-4af9-b019-a82efec35e51", ["name"] = "create probe.txt file", ["status"] = "busy",
            ["state"] = "working",
        }, edit);

    private static AgentEntry Build(Dictionary<string, object?> fields, Action<Dictionary<string, object?>>? edit)
    {
        edit?.Invoke(fields);
        JsonElement? waitingFor = null;
        if (fields.TryGetValue("waitingFor", out var raw) && raw is not null)
        {
            waitingFor = JsonSerializer.SerializeToElement(raw);
        }
        return new AgentEntry
        {
            SessionId = (string?)fields.GetValueOrDefault("sessionId"),
            Id = (string?)fields.GetValueOrDefault("id"),
            Kind = (string?)fields.GetValueOrDefault("kind"),
            Pid = (int?)fields.GetValueOrDefault("pid"),
            Cwd = (string?)fields.GetValueOrDefault("cwd"),
            Name = (string?)fields.GetValueOrDefault("name"),
            Status = (string?)fields.GetValueOrDefault("status"),
            StartedAt = (double?)fields.GetValueOrDefault("startedAt"),
            State = (string?)fields.GetValueOrDefault("state"),
            WaitingFor = waitingFor,
        };
    }

    private static AgentSession? ById(IReadOnlyList<AgentSession> sessions, string sessionId) =>
        sessions.FirstOrDefault(s => s.SessionId == sessionId);

    [Fact]
    public void Lists_terminal_and_background_sessions_with_their_state()
    {
        var output = AgentsTracker.Track([Interactive(), Background()], [], 5000);

        var terminal = ById(output, Interactive().SessionId!)!;
        Assert.Equal("interactive", terminal.Kind);
        Assert.Equal(25296, terminal.Pid);
        Assert.Equal("busy", terminal.Status);
        Assert.Null(terminal.State);

        var background = ById(output, Background().SessionId!)!;
        Assert.Equal("background", background.Kind);
        Assert.Equal("349831c7", background.Id);
        Assert.Equal("working", background.State);
        Assert.Equal(2000, background.Since); // a session first seen working has been working since it started
    }

    [Fact]
    public void A_background_session_the_widget_watched_finish_is_a_new_result_once()
    {
        var after5000 = AgentsTracker.Track([Background()], [], 5000);
        var doneAt9000 = AgentsTracker.Track([Background(f => { f["state"] = "done"; f["status"] = "idle"; })], after5000, 9000);
        var finished = ById(doneAt9000, Background().SessionId!)!;
        Assert.True(finished.Fresh);
        Assert.Equal(9000, finished.Since);

        var stillDone = AgentsTracker.Track([Background(f => { f["state"] = "done"; f["status"] = "idle"; })], doneAt9000, 60000);
        var later = ById(stillDone, Background().SessionId!)!;
        Assert.True(later.Fresh, "still the same unseen result");
        Assert.Equal(9000, later.Since); // the time it finished does not move
    }

    [Fact]
    public void A_background_session_found_already_finished_does_not_light_green()
    {
        // After a reboot every old session would otherwise look like a brand-new result.
        var found = ById(AgentsTracker.Track([Background(f => { f["state"] = "done"; f["status"] = "idle"; })], [], 5000), Background().SessionId!)!;
        Assert.False(found.Fresh);
    }

    [Fact]
    public void A_finished_background_session_leaves_the_widget_after_12_hours()
    {
        var working = AgentsTracker.Track([Background()], [], 0);
        var done = AgentsTracker.Track([Background(f => f["state"] = "done")], working, 1000);
        var stillAt11h = AgentsTracker.Track([Background(f => f["state"] = "done")], done, 1000 + 11 * Hour);
        Assert.NotNull(ById(stillAt11h, Background().SessionId!));
        var goneAt13h = AgentsTracker.Track([Background(f => f["state"] = "done")], done, 1000 + 13 * Hour);
        Assert.Null(ById(goneAt13h, Background().SessionId!));
    }

    [Fact]
    public void A_blocked_session_says_what_it_waits_for_and_a_stopped_one_is_dropped()
    {
        var output = AgentsTracker.Track(
        [
            // The value Claude Code 2.1.270 reported for a session blocked on AskUserQuestion.
            Background(f => { f["state"] = "blocked"; f["status"] = "waiting"; f["waitingFor"] = "input needed"; }),
            Background(f =>
            {
                f["sessionId"] = "aaaaaaaa-0000-0000-0000-000000000000"; f["id"] = "aaaaaaaa";
                f["state"] = "blocked"; f["waitingFor"] = new Dictionary<string, object?> { ["tool"] = "Bash" };
            }),
            Background(f => { f["sessionId"] = "bbbbbbbb-0000-0000-0000-000000000000"; f["id"] = "bbbbbbbb"; f["state"] = "stopped"; }),
            Background(f =>
            {
                f["sessionId"] = "cccccccc-0000-0000-0000-000000000000"; f["id"] = "cccccccc";
                f["state"] = "blocked"; f["waitingFor"] = "permission needed";
            }),
        ], [], 5000);

        Assert.Equal("Czeka na Twoją odpowiedź", ById(output, Background().SessionId!)!.WaitingFor);
        Assert.Equal("Bash", ById(output, "aaaaaaaa-0000-0000-0000-000000000000")!.WaitingFor); // unknown value shown as is
        Assert.Equal("Czeka na Twoją zgodę", ById(output, "cccccccc-0000-0000-0000-000000000000")!.WaitingFor);
        Assert.Null(ById(output, "bbbbbbbb-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void DescribeWaiting_stops_at_the_first_non_null_field_even_if_it_is_not_a_string()
    {
        // question is missing, message is null, description is a number — JS's `??` chain stops
        // there (first non-nullish value) and never reaches `tool`, even though tool is a string.
        var waitingFor = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["message"] = null,
            ["description"] = 5,
            ["tool"] = "Bash",
        });
        Assert.Equal("", AgentsTracker.DescribeWaiting(waitingFor));
    }

    [Fact]
    public void DescribeWaiting_falls_through_null_fields_to_the_next_one()
    {
        var waitingFor = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["question"] = null,
            ["message"] = null,
            ["tool"] = "Bash",
        });
        Assert.Equal("Bash", AgentsTracker.DescribeWaiting(waitingFor));
    }

    [Fact]
    public void FindClaude_earlier_directory_wins()
    {
        var npm = "C:\\npm";
        var local = "C:\\Users\\me\\.local\\bin";
        var path = $"{npm};\"{local}\";";
        const string pathExt = ".COM;.EXE;.BAT;.CMD;.VBS";

        Assert.Equal(Path.Combine(npm, "claude.cmd"), ClaudeLocator.FindClaude(
            Exists(Path.Combine(local, "claude.exe"), Path.Combine(npm, "claude.cmd")), path, pathExt));
    }

    [Fact]
    public void FindClaude_within_a_directory_pathext_decides()
    {
        var npm = "C:\\npm";
        var local = "C:\\Users\\me\\.local\\bin";
        var path = $"{npm};\"{local}\";";
        const string pathExt = ".COM;.EXE;.BAT;.CMD;.VBS";

        Assert.Equal(Path.Combine(npm, "claude.exe"), ClaudeLocator.FindClaude(
            Exists(Path.Combine(npm, "claude.cmd"), Path.Combine(npm, "claude.exe")), path, pathExt));
    }

    [Fact]
    public void FindClaude_a_quoted_directory_is_searched_too()
    {
        var npm = "C:\\npm";
        var local = "C:\\Users\\me\\.local\\bin";
        var path = $"{npm};\"{local}\";";
        const string pathExt = ".COM;.EXE;.BAT;.CMD;.VBS";

        Assert.Equal(Path.Combine(local, "claude.exe"), ClaudeLocator.FindClaude(Exists(Path.Combine(local, "claude.exe")), path, pathExt));
    }

    [Fact]
    public void FindClaude_returns_null_when_not_found()
    {
        var npm = "C:\\npm";
        var local = "C:\\Users\\me\\.local\\bin";
        var path = $"{npm};\"{local}\";";
        const string pathExt = ".COM;.EXE;.BAT;.CMD;.VBS";

        Assert.Null(ClaudeLocator.FindClaude(Exists(), path, pathExt));
    }

    private static Func<string, bool> Exists(params string[] files) => path => files.Contains(path);
}
