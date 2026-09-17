using ClaudeWidget.Core;

namespace ClaudeWidget.Tests.Hook;

// Port of tests/hook.test.mjs, case by case: drives HookEngine.Handle the way ClaudeWidgetHook.exe
// hook does, against a throwaway state directory, and reads the resulting state file back.
public sealed class HookEngineTests
{
    private const string Bash = """{"tool_name":"Bash","tool_input":{"command":"npm test","description":"Run tests"}}""";

    [Fact]
    public void A_full_turn_with_a_permission_prompt_walks_gotowe_pracuje_czeka_pracuje_gotowe()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "s1");

        var started = hook.Send("SessionStart");
        Assert.Equal(SessionStates.Done, started!.State);
        Assert.False(started.Fresh, "a new session is idle, not a result to review");

        var working = hook.Send("UserPromptSubmit");
        Assert.Equal(SessionStates.Working, working!.State);
        Assert.Equal("C:\\apps\\api", working.Cwd);

        var waiting = hook.Send("PermissionRequest", HookHarness.Extra(Bash));
        Assert.Equal(SessionStates.Waiting, waiting!.State);
        Assert.Equal("Zgoda: Bash · npm test", waiting.Detail);

        // A different tool finishing during the prompt must not turn the red light off.
        var stillWaiting = hook.Send("PostToolUse", HookHarness.Extra("""{"tool_name":"Read","tool_input":{"file_path":"a.txt"},"tool_response":{}}"""));
        Assert.Equal(SessionStates.Waiting, stillWaiting!.State);

        var resumed = hook.Send("PostToolUse", HookHarness.Extra("""{"tool_name":"Bash","tool_input":{"command":"npm test","description":"Run tests"},"tool_response":{"stdout":"ok"}}"""));
        Assert.Equal(SessionStates.Working, resumed!.State);
        Assert.Equal(working.TurnStartedAt, resumed.Since); // the working timer continues from the start of the turn

        var done = hook.Send("Stop", HookHarness.Extra("""{"background_tasks":[]}"""));
        Assert.Equal(SessionStates.Done, done!.State);
        Assert.True(done.Fresh, "a finished turn is a result to review");
        var notified = hook.Send("Notification", HookHarness.Extra("""{"notification_type":"idle_prompt"}"""));
        Assert.True(notified!.Fresh, "idle_prompt does not mark a result as seen");

        Assert.Null(hook.Send("SessionEnd", HookHarness.Extra("""{"reason":"other"}""")));
    }

    [Fact]
    public void An_answered_AskUserQuestion_turns_the_light_back_to_working()
    {
        // Regression: the answers are merged into tool_input before PostToolUse, so a comparison of
        // the whole input never matched and the session stayed red after the user had answered.
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "q1");
        hook.Send("UserPromptSubmit");
        const string questions = """[{"question":"Push blokuje uruchomione api:serve. Co robimy?","header":"api:serve","options":[{"label":"Zatrzymaj"},{"label":"Sam zatrzymam"}],"multiSelect":false}]""";

        var asking = hook.Send("PermissionRequest", HookHarness.Extra(
            """{"tool_name":"AskUserQuestion","tool_input":{"questions":""" + questions + """}}"""));
        Assert.Equal(SessionStates.Waiting, asking!.State);
        Assert.Equal("Pytanie: Push blokuje uruchomione api:serve. Co robimy?", asking.Detail);

        var answered = hook.Send("PostToolUse", HookHarness.Extra(
            """{"tool_name":"AskUserQuestion","tool_input":{"questions":""" + questions +
            ""","answers":{"Push blokuje uruchomione api:serve. Co robimy?":"Zatrzymaj"}},"tool_response":{}}"""));
        Assert.Equal(SessionStates.Working, answered!.State);
    }

    [Fact]
    public void A_permitted_tool_that_fails_still_ends_the_wait()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "f1");
        hook.Send("UserPromptSubmit");
        hook.Send("PermissionRequest", HookHarness.Extra(Bash));
        var result = hook.Send("PostToolUseFailure", HookHarness.Extra("""{"tool_name":"Bash","tool_input":{"command":"npm test","description":"Run tests"},"error":"exit code 1"}"""));
        Assert.Equal(SessionStates.Working, result!.State);
    }

    [Fact]
    public void A_denied_permission_ends_the_wait_when_the_batch_resolves()
    {
        // A denial produces no PostToolUse at all; the batch event is the only sign.
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "d1");
        hook.Send("UserPromptSubmit");
        hook.Send("PermissionRequest", HookHarness.Extra(Bash));
        var result = hook.Send("PostToolBatch", HookHarness.Extra("""{"tool_calls":[{"tool_name":"Bash","tool_input":{"command":"npm test"},"tool_use_id":"t1","tool_response":"denied"}]}"""));
        Assert.Equal(SessionStates.Working, result!.State);
    }

    [Fact]
    public void An_auto_mode_denial_ends_the_wait()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "d2");
        hook.Send("UserPromptSubmit");
        hook.Send("PermissionRequest", HookHarness.Extra(Bash));
        var result = hook.Send("PermissionDenied", HookHarness.Extra("""{"tool_name":"Bash","tool_input":{"command":"npm test"},"tool_use_id":"t1","reason":"classifier"}"""));
        Assert.Equal(SessionStates.Working, result!.State);
    }

    [Fact]
    public void A_resolved_batch_does_not_clear_a_red_light_caused_by_an_API_error()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "e1");
        hook.Send("UserPromptSubmit");
        hook.Send("StopFailure", HookHarness.Extra("""{"error":"rate_limit"}"""));
        var result = hook.Send("PostToolBatch", HookHarness.Extra("""{"tool_calls":[]}"""));
        Assert.Equal(SessionStates.Waiting, result!.State);
    }

    [Fact]
    public void A_waiting_state_written_by_the_previous_hook_version_is_still_resolved()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "old.state.json"),
            """{"state":"czeka","since":1,"turnStartedAt":1,"detail":"Zgoda: AskUserQuestion","pendingTool":"AskUserQuestion\u0000{\"questions\":[]}","updatedAt":1}""");
        var hook = new HookHarness(dir.Path, "old");
        var result = hook.Send("PostToolUse", HookHarness.Extra("""{"tool_name":"AskUserQuestion","tool_input":{"questions":[],"answers":{}}}"""));
        Assert.Equal(SessionStates.Working, result!.State);
    }

    [Fact]
    public void A_finished_turn_keeps_the_first_sentence_of_the_answer_as_a_preview()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "r1");
        hook.Send("UserPromptSubmit");
        var done = hook.Send("Stop", HookHarness.Extra("""
            {"background_tasks":[],"last_assistant_message":"## Podsumowanie\n\n**IT-901** jest zrobione: alert, kolejka i baner. Czekam na decyzję o commicie."}
            """));
        Assert.Equal("Podsumowanie", done!.Summary);
        var second = hook.Send("Stop", HookHarness.Extra("""{"background_tasks":[],"last_assistant_message":"**IT-901** jest zrobione: alert i baner. Czekam na decyzję."}"""));
        Assert.Equal("IT-901 jest zrobione: alert i baner.", second!.Summary);
    }

    [Fact]
    public void The_Claude_Code_process_id_is_recorded_with_the_state()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "p1") { Pid = 12345 };
        var state = hook.Send("UserPromptSubmit");
        Assert.Equal(12345, state!.Pid);
    }

    [Fact]
    public void A_session_picked_up_by_a_new_process_records_the_new_process_id_and_write_time()
    {
        // The widget treats a process that started after the last write as a stranger reusing the PID.
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "r1.state.json"),
            """{"state":"pracuje","since":1,"turnStartedAt":1,"cwd":"C:\\apps\\api","pid":1,"updatedAt":5}""");
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hook = new HookHarness(dir.Path, "r1") { Pid = 999 };
        var state = hook.Send("PostToolUse", HookHarness.Extra("""{"tool_name":"Read","tool_input":{"file_path":"a.txt"},"tool_response":{}}"""));
        Assert.Equal(SessionStates.Working, state!.State); // an event that changes nothing keeps the state
        Assert.Equal(999, state.Pid);
        Assert.True(state.UpdatedAt >= before);
    }

    [Fact]
    public void A_turn_that_ends_with_work_in_the_background_stays_yellow()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "b1");
        var working = hook.Send("UserPromptSubmit");
        var paused = hook.Send("Stop", HookHarness.Extra("""
            {"background_tasks":[
                {"id":"t1","type":"subagent","status":"running","description":"review"},
                {"id":"t2","type":"shell","status":"running","command":"gh pr checks --watch"}
            ]}
            """));
        Assert.Equal(SessionStates.Working, paused!.State);
        Assert.Equal(2, paused.Background);
        Assert.Equal("w tle: 2 zadania", paused.Detail);
        Assert.Equal(working!.TurnStartedAt, paused.Since);

        // Silence in the terminal while agents work is not the end of the turn.
        var idle = hook.Send("Notification", HookHarness.Extra("""{"notification_type":"idle_prompt"}"""));
        Assert.Equal(SessionStates.Working, idle!.State);

        // The background work wakes the session; its next Stop settles the state.
        var finished = hook.Send("Stop", HookHarness.Extra("""{"background_tasks":[]}"""));
        Assert.Equal(SessionStates.Done, finished!.State);
    }

    [Fact]
    public void The_transcript_path_is_kept_for_spotting_turns_interrupted_with_Esc()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "t1");
        var working = hook.Send("UserPromptSubmit", HookHarness.Extra("""{"transcript_path":"C:\\Users\\u\\.claude\\projects\\api\\t1.jsonl"}"""));
        Assert.Equal(@"C:\Users\u\.claude\projects\api\t1.jsonl", working!.TranscriptPath);

        var waiting = hook.Send("Notification", HookHarness.Extra("""{"notification_type":"permission_prompt"}"""));
        Assert.Equal(working.TranscriptPath, waiting!.TranscriptPath);
    }

    [Fact]
    public void A_monitor_alone_does_not_keep_the_session_yellow()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "b2");
        hook.Send("UserPromptSubmit");
        var result = hook.Send("Stop", HookHarness.Extra("""{"background_tasks":[{"id":"m1","type":"monitor","status":"running"}]}"""));
        Assert.Equal(SessionStates.Done, result!.State);
    }

    [Fact]
    public void Idle_prompt_ends_a_turn_that_was_interrupted_without_a_Stop_event_as_already_seen()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "s2");
        hook.Send("UserPromptSubmit");
        var idle = hook.Send("Notification", HookHarness.Extra("""{"notification_type":"idle_prompt"}"""));
        Assert.Equal(SessionStates.Done, idle!.State);
        Assert.False(idle.Fresh);
    }

    [Fact]
    public void Permission_prompt_notification_turns_the_light_red_and_the_resolved_batch_turns_it_off()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "s3");
        hook.Send("UserPromptSubmit");
        var waiting = hook.Send("Notification", HookHarness.Extra("""{"notification_type":"permission_prompt","message":"Claude needs your permission"}"""));
        Assert.Equal(SessionStates.Waiting, waiting!.State);
        Assert.Equal("Potrzebna Twoja decyzja", waiting.Detail);
        var resumed = hook.Send("PostToolBatch", HookHarness.Extra("""{"tool_calls":[]}"""));
        Assert.Equal(SessionStates.Working, resumed!.State);
    }

    [Fact]
    public void An_API_error_that_ends_the_turn_asks_for_attention()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "s4");
        hook.Send("UserPromptSubmit");
        var failed = hook.Send("StopFailure", HookHarness.Extra("""{"error":"rate_limit"}"""));
        Assert.Equal(SessionStates.Waiting, failed!.State);
        Assert.Equal("Błąd API: rate_limit", failed.Detail);
    }

    [Fact]
    public void File_tools_are_described_by_file_name_long_commands_are_shortened()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "s5");
        var edit = hook.Send("PermissionRequest", HookHarness.Extra("""{"tool_name":"Edit","tool_input":{"file_path":"C:\\apps\\api\\src\\very\\deep\\cancel.ts"}}"""));
        Assert.Equal("Zgoda: Edit · cancel.ts", edit!.Detail);

        var longCommand = "echo " + new string('x', 80) + "\nsecond line";
        var bash = hook.Send("PermissionRequest", HookHarness.Extra(
            """{"tool_name":"Bash","tool_input":{"command":""" + System.Text.Json.JsonSerializer.Serialize(longCommand) + """}}"""));
        Assert.True(bash!.Detail!.EndsWith('…') && bash.Detail.Length <= "Zgoda: Bash · ".Length + 40, bash.Detail);

        var plan = hook.Send("PermissionRequest", HookHarness.Extra("""{"tool_name":"ExitPlanMode","tool_input":{"plan":"x"}}"""));
        Assert.Equal("Plan do zatwierdzenia", plan!.Detail);
    }

    [Fact]
    public void A_hostile_session_id_cannot_write_outside_the_state_directory()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "..\\..\\evil");
        hook.Send("UserPromptSubmit");
        var names = Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName).Cast<string>().ToArray();
        Assert.Equal(["evil.state.json"], names);
    }

    [Fact]
    public void SessionEnd_removes_every_file_of_the_session_including_the_widget_seen_marker()
    {
        using var dir = new TempDir();
        var hook = new HookHarness(dir.Path, "s8");
        hook.Send("UserPromptSubmit");
        var paths = new WidgetPaths(dir.Path);
        JsonStore.Write(paths.SessionFile("s8", SessionFileKind.Usage)!, new SessionUsage { UpdatedAt = 1 }, StateJson.Default.SessionUsage);
        JsonStore.Write(paths.SessionFile("s8", SessionFileKind.Seen)!, new SeenMarker { SeenAt = 1 }, StateJson.Default.SeenMarker);
        Assert.Equal(3, Directory.GetFileSystemEntries(dir.Path).Length);
        hook.Send("SessionEnd", HookHarness.Extra("""{"reason":"clear"}"""));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }
}
