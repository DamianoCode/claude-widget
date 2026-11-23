using System.Text.Json;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Agents;
using ClaudeWidget.Core.Sessions;
using ClaudeWidget.Core.StatusLine;

namespace ClaudeWidget.Tests.Sessions;

public sealed class TranscriptContextTests
{
    // Wpisy w kształcie transkryptu Claude Code (jeden JSON na linię, bez spacji).
    internal static string Assistant(int input, int cacheCreation, int cacheRead, string model = "claude-opus-5-5", bool sidechain = false, long? at = null) =>
        "{\"type\":\"assistant\",\"isSidechain\":" + (sidechain ? "true" : "false")
        + (at is long ms ? ",\"timestamp\":\"" + DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") + "\"" : "")
        + ",\"message\":{\"model\":\"" + model + "\",\"role\":\"assistant\",\"usage\":{\"input_tokens\":" + input
        + ",\"cache_creation_input_tokens\":" + cacheCreation + ",\"cache_read_input_tokens\":" + cacheRead + ",\"output_tokens\":685}}}";

    internal static string Compact(long? at = null) =>
        "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"isSidechain\":false"
        + (at is long ms ? ",\"timestamp\":\"" + DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O") + "\"" : "") + "}";

    private const string User = """{"type":"user","isSidechain":false,"message":{"role":"user","content":"dalej"}}""";

    private static string Big(int kib) => "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', kib * 1024) + "\"}}";

    [Fact]
    public void Context_is_the_input_of_the_last_main_reply()
    {
        var reading = TranscriptContext.Find([Assistant(5, 100, 1000), User, Assistant(2, 2728, 212370, at: 1_795_000_000_000), User]);

        Assert.Equal(215_100, reading!.Tokens);
        Assert.Equal("claude-opus-5-5", reading.Model);
        Assert.Equal(1_795_000_000_000, reading.At);
    }

    [Fact]
    public void Subagent_and_synthetic_replies_are_skipped()
    {
        var reading = TranscriptContext.Find([
            Assistant(1, 0, 50_000),
            Assistant(1, 0, 900_000, sidechain: true),
            Assistant(0, 0, 0, model: "<synthetic>"),
            "{\"type\":\"assistant\",urwane",
        ]);

        Assert.Equal(50_001, reading!.Tokens);
    }

    [Fact]
    public void Compaction_after_the_last_reply_means_unknown_until_the_next_one()
    {
        var compacted = TranscriptContext.Find([Assistant(1, 0, 180_000), Compact(), User]);
        Assert.NotNull(compacted);
        Assert.Null(compacted.Tokens);

        var after = TranscriptContext.Find([Assistant(1, 0, 180_000), Compact(), User, Assistant(3, 9000, 12000)]);
        Assert.Equal(21_003, after!.Tokens);
    }

    [Fact]
    public void Without_any_reply_nothing_is_found() => Assert.Null(TranscriptContext.Find([User]));

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
    public void Appended_lines_without_a_reply_keep_the_previous_reading()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        File.WriteAllLines(path, [Assistant(1, 0, 10_000)]);
        var reader = new TranscriptContext();
        Assert.Equal(10_001, reader.Read(path)!.Tokens);

        // Dopisany duży wynik narzędzia i linia jeszcze w trakcie zapisu (bez końca wiersza).
        File.AppendAllText(path, Big(700) + "\n" + "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tok");
        Assert.Equal(10_001, reader.Read(path)!.Tokens);

        // Dokończona linia jest czytana przy następnym odczycie.
        File.AppendAllText(path, "ens\":7,\"cache_read_input_tokens\":30000}}}\n");
        Assert.Equal(30_007, reader.Read(path)!.Tokens);

        File.AppendAllLines(path, [Compact()]);
        Assert.Null(reader.Read(path)!.Tokens);
    }

    [Fact]
    public void Replaced_or_truncated_file_is_read_from_scratch()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        File.WriteAllLines(path, [User, User, User, Assistant(1, 0, 90_000)]);
        var reader = new TranscriptContext();
        Assert.Equal(90_001, reader.Read(path)!.Tokens);

        File.WriteAllLines(path, [Assistant(1, 0, 5_000)]);
        Assert.Equal(5_001, reader.Read(path)!.Tokens);
    }

    [Fact]
    public void Reply_far_back_behind_a_large_tool_result_is_still_found()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        File.WriteAllLines(path, [Assistant(1, 0, 30_000), Big(700), Big(3000)]);

        Assert.Equal(30_001, new TranscriptContext().Read(path)!.Tokens);
    }

    [Fact]
    public void Reply_beyond_the_scan_limit_is_not_searched_for()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        File.WriteAllLines(path, [Assistant(1, 0, 30_000), Big(1500), Big(1500), Big(1500), Big(1500), Big(1500), Big(1500)]);

        Assert.Null(new TranscriptContext().Read(path));
    }

    [Fact]
    public void Multibyte_text_split_across_read_chunks_is_parsed()
    {
        using var dir = new TempDir();
        var path = dir.File("s1.jsonl");
        var reply = Assistant(1, 0, 42_000).Replace("\"role\":\"assistant\"", "\"role\":\"assistant\",\"note\":\"" + string.Concat(Enumerable.Repeat("zażółć ", 30_000)) + "\"");
        File.WriteAllLines(path, [reply, User]);

        Assert.Equal(42_001, new TranscriptContext().Read(path)!.Tokens);
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
    private static ContextWindowMap Map(string model, params double[] sizes) => new() { Models = { [model] = [.. sizes] } };

    [Fact]
    public void Single_known_window_from_statusline_gives_an_exact_percentage()
    {
        var known = Map("claude-opus-5-5", 1_000_000);
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
    public void Current_models_have_their_documented_window_without_any_statusline()
    {
        // IT-1194: Orca pokazywała 125,4 tys. / 1 mln, widżet ≈62% (zgadnięte 200 tys.).
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("claude-opus-5-5", 125_400, null));
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("claude-fable-5-2", 50_000, null));
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("claude-sonnet-5-5", 50_000, null));
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("claude-haiku-5-5", 50_000, null));
        Assert.Equal((1_000_000d, false), ContextWindows.Resolve("claude-opus-4-6", 50_000, null));
        Assert.Equal((200_000d, false), ContextWindows.Resolve("claude-haiku-4-5", 50_000, null));
        Assert.Equal((200_000d, true), ContextWindows.Resolve("claude-opus-4-5", 50_000, null));
    }

    [Fact]
    public void Window_seen_in_statusline_overrides_the_documented_default()
    {
        Assert.Equal((200_000d, false), ContextWindows.Resolve("claude-opus-5-5", 125_400, Map("claude-opus-5-5", 200_000)));
    }

    [Fact]
    public void Conversation_larger_than_the_known_window_bumps_it()
    {
        Assert.Equal((1_000_000d, true), ContextWindows.Resolve("claude-sonnet-5-5", 300_000, Map("claude-sonnet-5-5", 200_000)));
    }

    [Fact]
    public void Model_seen_with_two_windows_is_only_an_estimate()
    {
        // Ten sam model w wariancie [1m] i zwykłym — transkrypt ich nie odróżnia.
        var known = ContextWindows.With(ContextWindows.With(null, "same-model[1m]", 1_000_000), "same-model", 200_000)!;

        Assert.Equal((200_000d, true), ContextWindows.Resolve("same-model", 150_000, known));
        Assert.Equal((1_000_000d, true), ContextWindows.Resolve("same-model", 400_000, known));
    }

    [Fact]
    public void Learning_writes_only_on_change_and_keeps_variants()
    {
        var first = ContextWindows.With(null, "claude-opus-5-5[1m]", 1_000_000)!;
        Assert.Equal([1_000_000d], first.Models["claude-opus-5-5"]);
        Assert.Null(ContextWindows.With(first, "claude-opus-5-5", 1_000_000));
        Assert.Null(ContextWindows.With(first, null, 1_000_000));
        Assert.Null(ContextWindows.With(first, "claude-opus-5-5", null));
        Assert.Equal([1_000_000d, 200_000d], ContextWindows.With(first, "claude-opus-5-5", 200_000)!.Models["claude-opus-5-5"]);
        Assert.Equal([200_000d], ContextWindows.With(first, "claude-haiku-5-5", 200_000)!.Models["claude-haiku-5-5"]);
    }

    [Fact]
    public void Parallel_statuslines_do_not_lose_each_others_models()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);

        Parallel.For(0, 16, i => ContextWindows.Learn(paths, $"model-{i}", 200_000 + i));

        var map = JsonStore.Read(paths.ContextWindowsFile, ContextWindowJson.Default.ContextWindowMap)!;
        Assert.Equal(16, map.Models.Count);
    }

    [Fact]
    public void Blocked_map_file_does_not_stop_the_statusline_from_saving_limits()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        // Katalog w miejscu pliku — zapis mapy zawsze się nie uda.
        Directory.CreateDirectory(paths.ContextWindowsFile);
        using var input = JsonDocument.Parse("""
            { "session_id": "s1", "model": { "id": "claude-x" },
              "context_window": { "used_percentage": 10, "context_window_size": 200000 },
              "rate_limits": { "seven_day": { "used_percentage": 41, "resets_at": 1795700000 } } }
            """);

        StatusLineEngine.Record(paths, input.RootElement, 1000);

        Assert.Equal(41, JsonStore.Read(paths.LimitsFile, StateJson.Default.AccountLimits)!.SevenDay!.Pct);
        Assert.Equal(10, JsonStore.Read(paths.SessionFile("s1", SessionFileKind.Usage)!, StateJson.Default.SessionUsage)!.ContextPct);
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
        Assert.False(session.ContextEstimated);
        Assert.Equal(21.51, session.ContextPct!.Value, 2);
        Assert.Equal("215 tys. / 1 mln", SessionFormatting.GetContextNote(session));
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
    public void Transcript_newer_than_a_quiet_statusline_wins()
    {
        // Sesja zaczęta w CLI, wznowiona w Orce: usage.json stoi, transkrypt rośnie.
        using var dir = new TempDir();
        var paths = new WidgetPaths(Path.Combine(dir.Path, "state"));
        var transcript = dir.File("s.jsonl");
        File.WriteAllLines(transcript, [TranscriptContextTests.Assistant(0, 0, 150_000, at: 100_000)]);
        WriteState(paths, "s", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 111, UpdatedAt = 1000, TranscriptPath = transcript });
        JsonStore.Write(paths.SessionFile("s", SessionFileKind.Usage)!,
            new SessionUsage { ContextPct = 5, ContextTokens = 10_000, ContextSize = 200_000, UpdatedAt = 1000 }, StateJson.Default.SessionUsage);

        var session = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: 100_000).Single();
        Assert.Equal(150_000, session.ContextTokens);
        Assert.Equal(15, session.ContextPct);

        // Streszczenie po wznowieniu — dawny odczyt statusline też już nie obowiązuje.
        File.AppendAllLines(transcript, [TranscriptContextTests.Compact(at: 200_000)]);
        var compacted = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: 200_000).Single();
        Assert.Null(compacted.ContextPct);
    }

    [Fact]
    public void Statusline_that_saw_the_last_reply_stays_exact()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(Path.Combine(dir.Path, "state"));
        var transcript = dir.File("s.jsonl");
        File.WriteAllLines(transcript, [TranscriptContextTests.Assistant(0, 0, 150_000, at: 100_000)]);
        WriteState(paths, "s", new SessionState { State = SessionStates.Working, Since = 1000, Pid = 111, UpdatedAt = 1000, TranscriptPath = transcript });
        JsonStore.Write(paths.SessionFile("s", SessionFileKind.Usage)!,
            new SessionUsage { ContextPct = 15, ContextTokens = 150_000, ContextSize = 1_000_000, UpdatedAt = 100_500 }, StateJson.Default.SessionUsage);

        var session = new SessionAggregator(paths, new FakeProcessProbe().Alive(111)).GetSessions(nowMs: 500_000).Single();

        Assert.Equal(15, session.ContextPct);
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
        Assert.Equal(4, session.ContextPct);
    }
}
