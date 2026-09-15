using ClaudeWidget.Core;
using ClaudeWidget.Core.StatusLine;
using ClaudeWidget.Tests.Hook;

namespace ClaudeWidget.Tests.StatusLine;

// Port of the statusline cases in tests/hook.test.mjs: StatusLineEngine.Record + Render together
// mirror what ClaudeWidgetHook.exe statusline does with one line of stdin JSON.
public sealed class StatusLineEngineTests
{
    [Fact]
    public void The_status_line_records_context_and_account_limits_and_prints_one_line()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        var input = JsonInput.Parse("""
            {
              "session_id": "s6",
              "session_name": "Widżet sygnalizatora",
              "cwd": "C:\\apps\\claudius",
              "workspace": { "current_dir": "C:\\apps\\claudius", "project_dir": "C:\\apps\\claudius" },
              "model": { "display_name": "Opus 5" },
              "context_window": { "used_percentage": 38.4, "total_input_tokens": 384000, "context_window_size": 1000000 },
              "rate_limits": {
                "five_hour": { "used_percentage": 23.5, "resets_at": 1757868000 },
                "seven_day": { "used_percentage": 41.2, "resets_at": 1758178800 }
              }
            }
            """);

        StatusLineEngine.Record(paths, input, 1000);
        var line = StatusLineEngine.Render(input);

        Assert.Equal("Opus 5 · kontekst 38% · 5 h 24% · tydzień 41%", line);

        var usage = JsonStore.Read(paths.SessionFile("s6", SessionFileKind.Usage)!, StateJson.Default.SessionUsage);
        Assert.Equal("Widżet sygnalizatora", usage!.Name);
        Assert.Equal("claudius", usage.Project);
        Assert.Equal(38.4, usage.ContextPct);
        Assert.Equal(1000000, usage.ContextSize);

        var limits = JsonStore.Read(paths.LimitsFile, StateJson.Default.AccountLimits);
        Assert.Equal(23.5, limits!.FiveHour!.Pct);
        Assert.Equal(1757868000, limits.FiveHour.ResetsAt);
        Assert.Equal(41.2, limits.SevenDay!.Pct);
        Assert.Equal(1758178800, limits.SevenDay.ResetsAt);
    }

    [Fact]
    public void Without_rate_limits_API_key_before_the_first_reply_no_limits_file_is_written()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);
        var input = JsonInput.Parse("""{"session_id":"s7","model":{"display_name":"Sonnet 5"},"context_window":{"used_percentage":5}}""");

        StatusLineEngine.Record(paths, input, 1000);
        var line = StatusLineEngine.Render(input);

        Assert.Equal("Sonnet 5 · kontekst 5%", line);
        Assert.False(File.Exists(paths.LimitsFile));
    }

    [Fact]
    public void Headless_sessions_still_render_the_status_line_itself()
    {
        // record() is gated on AttendedSession in Program.cs, but render() must always happen —
        // the status line itself has to show even for `claude -p` / SDK sessions.
        var input = JsonInput.Parse("""{"session_id":"h1","model":{"display_name":"Opus 5"}}""");
        Assert.Equal("Opus 5", StatusLineEngine.Render(input));
    }
}
