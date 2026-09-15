using System.Diagnostics;
using System.Text.Json;

namespace ClaudeWidget.Tests.Hook;

// ClaudeWidgetHook.exe uruchomiony tak, jak robi to Claude Code: proces potomny z JSON-em na stdin.
public class HookCliTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "ClaudeWidgetHook.exe");

    private static (int ExitCode, string Stdout) Run(string arguments, string stdin, string stateDir)
    {
        var info = new ProcessStartInfo(Exe, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            // Claude Code czyta linię statusu jako UTF-8 — „·” w innym kodowaniu byłoby błędem.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["CLAUDE_WIDGET_STATE_DIR"] = stateDir;
        info.Environment["CLAUDE_CODE_ENTRYPOINT"] = "cli";
        info.Environment["CLAUDE_CODE_SESSION_ATTENDED"] = "1";
        using var process = Process.Start(info)!;
        try
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // proces mógł skończyć, zanim przeczytał wejście (np. nieznane polecenie)
        }
        var stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10_000), "the hook must finish quickly");
        return (process.ExitCode, stdout);
    }

    [Fact]
    public void The_hook_records_the_state_with_the_parent_process_id_and_prints_nothing()
    {
        using var dir = new TempDir();
        var (exitCode, stdout) = Run("hook", """{"hook_event_name":"UserPromptSubmit","session_id":"cli1","cwd":"C:\\apps\\api"}""", dir.Path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        using var state = JsonDocument.Parse(File.ReadAllText(dir.File("cli1.state.json")));
        Assert.Equal("pracuje", state.RootElement.GetProperty("state").GetString());
        Assert.Equal(Environment.ProcessId, state.RootElement.GetProperty("pid").GetInt32());
    }

    [Theory]
    [InlineData("hook", "{ not json")]
    [InlineData("", "{}")]
    [InlineData("unknown", "{}")]
    public void Malformed_input_or_an_unknown_command_exits_0_silently(string arguments, string stdin)
    {
        using var dir = new TempDir();
        var (exitCode, stdout) = Run(arguments, stdin, dir.Path);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void The_status_line_prints_one_line()
    {
        using var dir = new TempDir();
        var (exitCode, stdout) = Run("statusline", """{"session_id":"cli2","model":{"display_name":"Opus 5"},"context_window":{"used_percentage":10.4}}""", dir.Path);

        Assert.Equal(0, exitCode);
        Assert.Equal("Opus 5 · kontekst 10%", stdout);
    }
}
