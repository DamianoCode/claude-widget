using System.Diagnostics;
using System.Text.Json;

namespace ClaudeWidget.Tests.Hook;

// ClaudeWidgetHook.exe uruchomiony tak, jak robi to Claude Code: proces potomny z JSON-em na stdin.
public class HookCliTests
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "ClaudeWidgetHook.exe");

    // Testy same chodzą czasem w terminalu albo czacie Orca — jego zmienne nie mogą przeciec do hooka.
    private static readonly IReadOnlyDictionary<string, string?> OutsideOrca = new Dictionary<string, string?>
    {
        ["ORCA_AGENT_SESSION_ID"] = null,
        ["ORCA_TERMINAL_HANDLE"] = null,
    };

    private static (int ExitCode, string Stdout) Run(string arguments, string stdin, string stateDir, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var info = new ProcessStartInfo(Exe, arguments)
        {
            RedirectStandardInput = true,
            // Claude Code pisze na wejście UTF-8 bez BOM; domyślnie byłaby tu strona kodowa konsoli.
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            RedirectStandardOutput = true,
            // Claude Code czyta linię statusu jako UTF-8 — „·” w innym kodowaniu byłoby błędem.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["CLAUDE_WIDGET_STATE_DIR"] = stateDir;
        info.Environment["CLAUDE_CODE_ENTRYPOINT"] = "cli";
        info.Environment["CLAUDE_CODE_SESSION_ATTENDED"] = "1";
        foreach (var (name, value) in OutsideOrca.Concat(environment ?? new Dictionary<string, string?>()))
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }
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

    [Fact]
    public void A_cli_session_outside_orca_writes_no_orca_fields()
    {
        using var dir = new TempDir();
        Run("hook", """{"hook_event_name":"UserPromptSubmit","session_id":"cli4","cwd":"C:\\apps\\api"}""", dir.Path);

        var json = File.ReadAllText(dir.File("cli4.state.json"));
        Assert.DoesNotContain("orca", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_headless_run_with_an_orca_chats_environment_but_not_started_by_orca_is_ignored()
    {
        // Środowisko hooka w czacie Orca 1.4.263 — tak samo widzi je `claude -p` odpalony z czatu.
        // Rodzicem hooka jest tu proces testów, nie Orca, jak w takim zagnieżdżonym przebiegu.
        using var dir = new TempDir();
        var orcaChat = new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "orca",
            ["CLAUDE_CODE_SESSION_ATTENDED"] = "0",
            ["ORCA_AGENT_SESSION_ID"] = "dfbd0007-1109-4766-9512-58f3bb8b4dfc",
        };

        var (exitCode, stdout) = Run("hook", """{"hook_event_name":"UserPromptSubmit","session_id":"orca1","cwd":"C:\\apps\\api"}""", dir.Path, orcaChat);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.False(File.Exists(dir.File("orca1.state.json")));
    }

    [Fact]
    public void A_cli_session_in_an_orca_terminal_is_recorded_with_its_terminal_handle()
    {
        using var dir = new TempDir();
        var orcaTerminal = new Dictionary<string, string?> { ["ORCA_TERMINAL_HANDLE"] = "term_8edd3d90-a6fc-4baf-a3d8-97e69f3c9126" };

        Run("hook", """{"hook_event_name":"UserPromptSubmit","session_id":"orca2","cwd":"C:\\apps\\api"}""", dir.Path, orcaTerminal);

        using var state = JsonDocument.Parse(File.ReadAllText(dir.File("orca2.state.json")));
        Assert.Equal("term_8edd3d90-a6fc-4baf-a3d8-97e69f3c9126", state.RootElement.GetProperty("orcaTerminal").GetString());
    }

    [Fact]
    public void A_headless_orca_run_without_an_agent_id_is_ignored()
    {
        using var dir = new TempDir();
        Run("hook", """{"hook_event_name":"UserPromptSubmit","session_id":"orca3","cwd":"C:\\apps\\api"}""", dir.Path,
            new Dictionary<string, string?> { ["CLAUDE_CODE_ENTRYPOINT"] = "orca" });

        Assert.False(File.Exists(dir.File("orca3.state.json")));
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
    public void The_relay_passes_its_input_through_unchanged_and_records_it_for_the_widget()
    {
        using var dir = new TempDir();
        const string input = """{"session_id":"cli3","workspace":{"project_dir":"C:\\apps\\żółw"},"context_window":{"used_percentage":42}}""";

        var (exitCode, stdout) = Run("tee", input, dir.Path);

        Assert.Equal(0, exitCode);
        Assert.Equal(input, stdout);
        Assert.True(File.Exists(dir.File("cli3.usage.json")));
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
