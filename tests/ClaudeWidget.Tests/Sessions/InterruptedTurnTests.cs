using ClaudeWidget.Core;
using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget.Tests.Sessions;

public class InterruptedTurnTests
{
    private static readonly DateTimeOffset TurnStart = new(2026, 9, 17, 9, 47, 0, TimeSpan.Zero);
    private static readonly long TurnStartMs = TurnStart.ToUnixTimeMilliseconds();

    private static string Stamp(int seconds) => TurnStart.AddSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    // Wpisy w kształcie tych, które Claude Code zapisuje w transkrypcie (skrócone).
    private static string Prompt(int s) =>
        $$"""{"parentUuid":null,"isSidechain":false,"type":"user","message":{"role":"user","content":"sprawdź aktualizacje"},"uuid":"u{{s}}","timestamp":"{{Stamp(s)}}"}""";

    private static string Assistant(int s, bool sidechain = false) =>
        $$"""{"parentUuid":"x","isSidechain":{{(sidechain ? "true" : "false")}},"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash","input":{} }]},"uuid":"a{{s}}","timestamp":"{{Stamp(s)}}"}""";

    private static string ToolResult(int s) =>
        $$"""{"parentUuid":"x","isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok","is_error":false}]},"uuid":"r{{s}}","timestamp":"{{Stamp(s)}}"}""";

    private static string Interrupt(int s, string text = "[Request interrupted by user]") =>
        $$"""{"parentUuid":"x","isSidechain":false,"promptId":"p","type":"user","message":{"role":"user","content":[{"type":"text","text":"{{text}}"}]},"uuid":"i{{s}}","timestamp":"{{Stamp(s)}}","interruptedMessageId":"msg_1"}""";

    private static string Attachment(int s) =>
        $$"""{"parentUuid":"x","isSidechain":false,"attachment":{"type":"total_tokens_reminder","text":"<total_tokens>1</total_tokens>"},"type":"attachment","uuid":"at{{s}}","timestamp":"{{Stamp(s)}}"}""";

    private const string PrLink = """{"type":"pr-link","sessionId":"s","prNumber":15}""";

    [Fact]
    public void A_turn_ending_with_the_interrupt_message_is_interrupted_at_that_moment()
    {
        long? at = InterruptedTurn.Find([Prompt(0), Assistant(5), ToolResult(8), Interrupt(12), Attachment(13), PrLink], TurnStartMs);

        Assert.Equal(TurnStartMs + 12_000, at);
    }

    [Fact]
    public void An_interrupted_tool_call_counts_too()
    {
        var toolInterrupt = $$"""{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"The user doesn't want to proceed with this tool use.","is_error":true},{"type":"text","text":"[Request interrupted by user for tool use]"}]},"timestamp":"{{Stamp(9)}}"}""";

        Assert.Equal(TurnStartMs + 9_000, InterruptedTurn.Find([Prompt(0), Assistant(5), toolInterrupt], TurnStartMs));
        Assert.NotNull(InterruptedTurn.Find([Prompt(0), Assistant(5), ToolResult(8), Interrupt(9, "[Request interrupted by user for tool use]")], TurnStartMs));
    }

    [Fact]
    public void Work_after_the_interrupt_means_the_session_is_working_again()
    {
        Assert.Null(InterruptedTurn.Find([Interrupt(3), Prompt(4), Assistant(6)], TurnStartMs));
        Assert.Null(InterruptedTurn.Find([Interrupt(3), Prompt(4)], TurnStartMs));
    }

    [Fact]
    public void An_interrupt_from_an_earlier_turn_does_not_count()
    {
        Assert.Null(InterruptedTurn.Find([Interrupt(-30), Attachment(-29)], TurnStartMs));
    }

    [Fact]
    public void Subagent_and_helper_entries_after_the_interrupt_are_ignored()
    {
        var meta = $$"""{"isSidechain":false,"isMeta":true,"type":"user","message":{"role":"user","content":"Caveat: local command"},"timestamp":"{{Stamp(14)}}"}""";

        Assert.NotNull(InterruptedTurn.Find([Prompt(0), Interrupt(12), Assistant(13, sidechain: true), meta], TurnStartMs));
    }

    private static string UserText(int s, string text) =>
        $$"""{"parentUuid":"x","isSidechain":false,"type":"user","message":{"role":"user","content":{{System.Text.Json.JsonSerializer.Serialize(text)}} },"timestamp":"{{Stamp(s)}}"}""";

    [Fact]
    public void Local_commands_typed_after_the_interrupt_do_not_start_a_turn()
    {
        string[] lines =
        [
            Prompt(0), Interrupt(12),
            UserText(20, "<command-name>/config</command-name>\n<command-message>config</command-message>\n<command-args></command-args>"),
            UserText(21, "<local-command-stdout>Config dialog dismissed</local-command-stdout>"),
            UserText(30, "<bash-input>git status</bash-input>"),
            UserText(31, "<bash-stdout>On branch main</bash-stdout><bash-stderr></bash-stderr>"),
        ];

        Assert.Equal(TurnStartMs + 12_000, InterruptedTurn.Find(lines, TurnStartMs));
    }

    [Fact]
    public void A_skill_command_after_the_interrupt_starts_a_turn()
    {
        var skill = UserText(20, "<command-message>claudius:implement</command-message>\n<command-name>/claudius:implement</command-name>");
        var body = $$"""{"isSidechain":false,"isMeta":true,"type":"user","message":{"role":"user","content":[{"type":"text","text":"Base directory for this skill"}]},"timestamp":"{{Stamp(20)}}"}""";

        Assert.Null(InterruptedTurn.Find([Prompt(0), Interrupt(12), skill, body], TurnStartMs));
    }

    [Fact]
    public void A_permission_prompt_rejected_with_Esc_is_no_longer_waiting_but_an_API_error_stays_red()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.File("state"));
        var transcript = dir.File("s1.jsonl");
        var rejected = $$"""{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"The user doesn't want to proceed with this tool use.","is_error":true}]},"timestamp":"{{Stamp(40)}}"}""";
        File.WriteAllLines(transcript, [Prompt(0), Assistant(30), rejected, Interrupt(40, "[Request interrupted by user for tool use]")]);
        using var pending = System.Text.Json.JsonDocument.Parse("""{"name":"Bash","input":{"command":"rm -rf dist"}}""");
        JsonStore.Write(paths.SessionFile("s1", SessionFileKind.State)!, new SessionState
        {
            State = SessionStates.Waiting, Since = TurnStartMs + 31_000, TurnStartedAt = TurnStartMs, Detail = "Zgoda: Bash",
            PendingTool = pending.RootElement.Clone(), Pid = 111, UpdatedAt = TurnStartMs + 31_000, TranscriptPath = transcript,
        }, StateJson.Default.SessionState);
        JsonStore.Write(paths.SessionFile("s2", SessionFileKind.State)!, new SessionState
        {
            State = SessionStates.Waiting, Since = TurnStartMs + 31_000, Detail = "Błąd API: overloaded",
            Pid = 111, UpdatedAt = TurnStartMs + 31_000, TranscriptPath = transcript,
        }, StateJson.Default.SessionState);

        var sessions = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: TurnStartMs + 60_000).ToDictionary(s => s.Id);

        Assert.Equal(SessionKinds.Idle, sessions["s1"].Kind);
        Assert.Equal(SessionKinds.Waiting, sessions["s2"].Kind);
    }

    [Fact]
    public void A_plain_turn_in_progress_is_not_interrupted()
    {
        Assert.Null(InterruptedTurn.Find([Prompt(0), Assistant(5), ToolResult(8)], TurnStartMs));
        Assert.Null(InterruptedTurn.Find([], TurnStartMs));
    }

    [Fact]
    public void Broken_or_unknown_lines_are_skipped_without_throwing()
    {
        string[] lines = [Prompt(0), Interrupt(12), """{"type":"user","message":""", "not json at all", """{"type":"user"}"""];

        // Ostatnia pełna wiadomość użytkownika nie ma treści — to nie przerwanie.
        Assert.Null(InterruptedTurn.Find(lines, TurnStartMs));
        Assert.NotNull(InterruptedTurn.Find(lines[..^1], TurnStartMs));
    }

    [Fact]
    public void Reads_only_the_end_of_a_large_transcript_and_follows_its_changes()
    {
        using var dir = new TempDir();
        var path = dir.File("session.jsonl");
        var filler = string.Join('\n', Enumerable.Range(0, 3000).Select(i => Attachment(1)));
        File.WriteAllText(path, Prompt(0) + "\n" + filler + "\n" + Assistant(5) + "\n");
        var probe = new InterruptedTurn();

        Assert.Null(probe.Check(path, TurnStartMs));

        File.AppendAllText(path, Interrupt(12) + "\n" + PrLink + "\n");
        Assert.Equal(TurnStartMs + 12_000, probe.Check(path, TurnStartMs));

        File.AppendAllText(path, Prompt(20) + "\n");
        Assert.Null(probe.Check(path, TurnStartMs));
    }

    [Fact]
    public void Missing_or_unexpected_paths_are_not_interruptions()
    {
        using var dir = new TempDir();
        var other = dir.File("notes.txt");
        File.WriteAllText(other, Interrupt(12));
        var probe = new InterruptedTurn();

        Assert.Null(probe.Check(null, TurnStartMs));
        Assert.Null(probe.Check(dir.File("missing.jsonl"), TurnStartMs));
        Assert.Null(probe.Check(other, TurnStartMs));
    }

    [Fact]
    public void The_aggregator_shows_an_interrupted_turn_as_idle_without_a_new_result()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.File("state"));
        var transcript = dir.File("s1.jsonl");
        File.WriteAllText(transcript, string.Join('\n', Prompt(0), Assistant(5), Interrupt(12)) + "\n");
        JsonStore.Write(paths.SessionFile("s1", SessionFileKind.State)!, new SessionState
        {
            State = SessionStates.Working, Since = TurnStartMs, TurnStartedAt = TurnStartMs, Detail = "x",
            Pid = 111, UpdatedAt = TurnStartMs, TranscriptPath = transcript,
        }, StateJson.Default.SessionState);
        // Ta sama sesja z zadaniami w tle pracuje dalej mimo przerwanej tury.
        JsonStore.Write(paths.SessionFile("s2", SessionFileKind.State)!, new SessionState
        {
            State = SessionStates.Working, Since = TurnStartMs, TurnStartedAt = TurnStartMs, Background = 2,
            Pid = 111, UpdatedAt = TurnStartMs, TranscriptPath = transcript,
        }, StateJson.Default.SessionState);
        var aggregator = new SessionAggregator(paths, new FakeProcessProbe().Alive(111));

        var sessions = aggregator.GetSessions(nowMs: TurnStartMs + 60_000).ToDictionary(s => s.Id);

        Assert.Equal(SessionKinds.Idle, sessions["s1"].Kind);
        Assert.Equal(TurnStartMs + 12_000, sessions["s1"].Since);
        Assert.Equal("", sessions["s1"].Detail);
        Assert.Equal(SessionKinds.Working, sessions["s2"].Kind);

        // Nowe polecenie: hook zaczyna nową turę, a przerwanie jest już starsze.
        JsonStore.Write(paths.SessionFile("s1", SessionFileKind.State)!, new SessionState
        {
            State = SessionStates.Working, Since = TurnStartMs + 30_000, TurnStartedAt = TurnStartMs + 30_000,
            Pid = 111, UpdatedAt = TurnStartMs + 30_000, TranscriptPath = transcript,
        }, StateJson.Default.SessionState);
        Assert.Equal(SessionKinds.Working, aggregator.GetSessions(nowMs: TurnStartMs + 61_000).Single(s => s.Id == "s1").Kind);
    }
}
