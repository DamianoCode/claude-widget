using System.Text.Json;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Agents;
using ClaudeWidget.Core.Sessions;
using ClaudeWidget.Core.StatusLine;

namespace ClaudeWidget.Tests.Sessions;

public sealed class TranscriptContextTests
{
    // Wpisy w kształcie transkryptu Claude Code (jeden JSON na linię, bez spacji).
    internal static string Assistant(int input, int cacheCreation, int cacheRead, string model = "claude-opus-5-5", bool sidechain = false) =>
        "{\"type\":\"assistant\",\"isSidechain\":" + (sidechain ? "true" : "false")
        + ",\"message\":{\"model\":\"" + model + "\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":" + input
        + ",\"cache_creation_input_tokens\":" + cacheCreation + ",\"cache_read_input_tokens\":" + cacheRead + ",\"output_tokens\":685}}}";

    private const string User = """{"type":"user","isSidechain":false,"message":{"role":"user","content":"dalej"}}""";
    private const string Compact = """{"type":"system","subtype":"compact_boundary","isSidechain":false}""";

    [Fact]
    public void Context_is_the_input_of_the_last_main_reply()
    {
        var usage = TranscriptContext.Find([Assistant(5, 100, 1000), User, Assistant(2, 2728, 212370), User], out var decided);

        Assert.True(decided);
        Assert.Equal(215_100, usage!.Tokens);
        Assert.Equal("claude-opus-5-5", usage.Model);
    }

    [Fact]
    public void Subagent_and_synthetic_replies_are_skipped()
    {
        var usage = TranscriptContext.Find([
            Assistant(1, 0, 50_000),
            Assistant(1, 0, 900_000, sidechain: true),
            Assistant(0, 0, 0, model: "<synthetic>"),
            "{\"type\":\"assistant\",urwane",
        ], out _);

        Assert.Equal(50_001, usage!.Tokens);
    }

    [Fact]
    public void Compaction_after_the_last_reply_means_unknown_until_the_next_one()
    {
        Assert.Null(TranscriptContext.Find([Assistant(1, 0, 180_000), Compact, User], out var decided));
        Assert.True(decided);

        var after = TranscriptContext.Find([Assistant(1, 0, 180_000), Compact, User, Assistant(3, 9000, 12000)], out _);
        Assert.Equal(21_003, after!.Tokens);
    }

    [Fact]
    public void Without_any_reply_nothing_is_decided()
    {
        Assert.Null(TranscriptContext.Find([User], out var decided));
        Assert.False(decided);
    }

    [Fact]
    public void Reads_the_file_and_rereads_only_after_a_change()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        File.WriteAllLines(path, [User, Assistant(1, 0, 10_000)]);
        var reader = new TranscriptContext();

        Assert.Equal(10_001, reader.Read(path)!.Tokens);
        File.AppendAllLines(path, [User, Assistant(1, 0, 20_000)]);
        Assert.Equal(20_001, reader.Read(path)!.Tokens);
        Assert.Null(reader.Read(dir.File("brak.jsonl")));
        Assert.Null(reader.Read(dir.File("s1.txt")));
    }

    [Fact]
    public void Reply_far_back_behind_a_large_tool_result_is_still_found()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        var big = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 700 * 1024) + "\"}}";
        File.WriteAllLines(path, [Assistant(1, 0, 30_000), big]);

        Assert.Equal(30_001, new TranscriptContext().Read(path)!.Tokens);
    }

    [Fact]
    public void Transcript_path_of_a_listed_session_follows_claude_code_folder_naming()
    {
        Assert.Equal(
            Path.Combine("C:\\cfg", "projects", "C--Users-Damian-orca-workspaces-claude-widget-frogfish", "0c054033-2a40.jsonl"),
            TranscriptContext.GuessPath("C:\\cfg", "C:\\Users\\Damian\\orca\\workspaces\\claude-widget\\frogfish", "0c054033-2a40"));
        Assert.Null(TranscriptContext.GuessPath("C:\\cfg", "", "id"));
        Assert.Null(TranscriptContext.GuessPath("C:\\cfg", "C:\\x", "..\\evil"));
    }
}

public sealed class ContextWindowsTests
{
    [Fact]
    public void Known_window_from_statusline_gives_an_exact_percentage()
    {
        var known = new ContextWindowMap { Models = { ["claude-opus-5-5"] = 1_000_000 } };
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("claude-opus-5-5", 215_100, known));
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("Claude-Opus-5-5[1m]", 215_100, known));
    }

    [Theory]
    [InlineData(150_000, 200_000)]
    [InlineData(200_000, 200_000)]
    [InlineData(215_100, 1_000_000)]
    public void Unknown_model_gets_an_estimated_window(double tokens, double size)
    {
        Assert.Equal((size, true), ContextWindows.Resolve("claude-nowy-1", tokens, null));
    }

    [Fact]
    public void Conversation_larger_than_the_known_window_bumps_it()
    {
        var known = new ContextWindowMap { Models = { ["claude-sonnet-5-5"] = 200_000 } };
        Assert.Equal((1_000_000d, true), ContextWindows.Resolve("claude-sonnet-5-5", 300_000, known));
    }

    [Fact]
    public void Learning_writes_only_on_change()
    {
        var first = ContextWindows.With(null, "claude-opus-5-5[1m]", 1_000_000)!;
        Assert.Equal(1_000_000, first.Models["claude-opus-5-5"]);
        Assert.Null(ContextWindows.With(first, "claude-opus-5-5", 1_000_000));
        Assert.Null(ContextWindows.With(first, null, 1_000_000));
        Assert.Null(ContextWindows.With(first, "claude-opus-5-5", null));
        Assert.Equal(200_000, ContextWindows.With(first, "claude-haiku-5-5", 200_000)!.Models["claude-haiku-5-5"]);
    }
}

public sealed class SessionContextFromTranscriptTests
{
    private static void WriteState(WidgetPaths paths, string id, SessionState state) =>
        JsonStore.Write(paths.SessionFile(id, SessionFileKind.State)!, state, StateJson.Default.SessionState);

    [Fact]
    public void Orca_session_without_statusline_gets_context_from_its_transcript()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(Path.Combine(dir.Path, "state"));
        var transcript = dir.File("orca.jsonl");
        File.WriteAllLines(transcript, [TranscriptContextTests.Assistant(2, 2728, 212370)]);
        WriteState(paths, "orca", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 111, UpdatedAt = 1000, TranscriptPath = transcript, OrcaSessionId = "a1" });

        var session = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: 2000).Single();

        Assert.Equal(215_100, session.ContextTokens);
        Assert.Equal(1_000_000, session.ContextSize);
        Assert.True(session.ContextEstimated);
        Assert.Equal(21.51, session.ContextPct!.Value, 2);
        Assert.Equal("215 tys. / ok. 1 mln", SessionFormatting.GetContextNote(session));
    }

    [Fact]
    public void Window_learned_from_a_cli_statusline_makes_the_orca_percentage_exact()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(Path.Combine(dir.Path, "state"));
        using var input = JsonDocument.Parse("""
            { "session_id": "cli", "model": { "id": "claude-sonnet-5-5", "display_name": "Sonnet 5.5" },
              "context_window": { "used_percentage": 10, "total_input_tokens": 20000, "context_window_size": 200000 } }
            """);
        StatusLineEngine.Record(paths, input.RootElement, 1000);
        var transcript = dir.File("orca.jsonl");
        File.WriteAllLines(transcript, [TranscriptContextTests.Assistant(0, 0, 50_000, model: "claude-sonnet-5-5")]);
        WriteState(paths, "orca", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 111, UpdatedAt = 1000, TranscriptPath = transcript });

        var session = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: 2000).Single(s => s.Id == "orca");

        Assert.False(session.ContextEstimated);
        Assert.Equal(25, session.ContextPct);
    }

    [Fact]
    public void Statusline_reading_wins_over_the_transcript()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(Path.Combine(dir.Path, "state"));
        var transcript = dir.File("cli.jsonl");
        File.WriteAllLines(transcript, [TranscriptContextTests.Assistant(0, 0, 50_000)]);
        WriteState(paths, "cli", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 111, UpdatedAt = 1000, TranscriptPath = transcript });
        JsonStore.Write(paths.SessionFile("cli", SessionFileKind.Usage)!,
            new SessionUsage { ContextPct = 38.4, ContextTokens = 76_800, ContextSize = 200_000, UpdatedAt = 1000 }, StateJson.Default.SessionUsage);

        var session = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: 2000).Single();

        Assert.Equal(38.4, session.ContextPct);
        Assert.False(session.ContextEstimated);
    }

    [Fact]
    public void Background_session_from_the_agents_list_reads_its_transcript_by_convention()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(Path.Combine(dir.Path, "state"));
        Directory.CreateDirectory(paths.StateDir);
        var config = Path.Combine(dir.Path, "claude");
        var transcript = TranscriptContext.GuessPath(config, "C:\\apps\\x", "bg-1")!;
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllLines(transcript, [TranscriptContextTests.Assistant(0, 0, 40_000)]);
        var agents = new AgentsSnapshot(true, 2000, [
            new AgentSession { SessionId = "bg-1", Id = "bg-1", Kind = "background", State = "working", Since = 1500, Cwd = "C:\\apps\\x" },
        ]);

        var session = new SessionAggregator(paths, new FakeProcessProbe()) { ClaudeConfigDir = config }.GetSessions(2000, agents).Single();

        Assert.True(session.IsBackground);
        Assert.Equal(20, session.ContextPct);
    }
}
