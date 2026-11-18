using ClaudeWidget.Core.Ide;

namespace ClaudeWidget.Tests.Ide;

/// <summary>Udawane CLI Orca: odpowiada według pierwszych słów polecenia i zapisuje, co wywołano.</summary>
internal sealed class FakeOrcaCli : IOrcaCli
{
    private readonly Dictionary<string, string> _replies = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    /// <summary>Odpowiedź (stdout <c>--json</c>) na polecenie, którego argumenty bez <c>--json</c> to <paramref name="command"/>.</summary>
    public FakeOrcaCli Reply(string command, string stdout)
    {
        _replies[command] = stdout;
        return this;
    }

    public Task<OrcaReply> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var command = string.Join(' ', arguments.Where(argument => argument != "--json"));
        Calls.Add(command);
        return Task.FromResult(_replies.TryGetValue(command, out var stdout)
            ? OrcaCli.Parse(stdout)
            : OrcaCli.Parse("""{"ok":false,"error":{"code":"selector_not_found"}}"""));
    }
}

public sealed class OrcaTests
{
    private const string Ok = """{"ok":true,"result":{}}""";
    private const string ChatWorkspace = "17a4d9ad-3f37-4802-914f-43ba7fb374ae::C:/Users/Damian/orca/workspaces/claude-widget/Orca";

    // Odpowiedź `orca agent show <id> --json` dla czatu, skopiowana z Orca 1.4.263.
    private const string AgentShowChat = """
        {"id":"x","ok":true,"result":{"orcaSessionId":"a1","kind":"chat","places":[{"orcaSessionId":"a1","kind":"chat","agent":"claude","state":"working",
        "worktreeId":"17a4d9ad-3f37-4802-914f-43ba7fb374ae::C:/Users/Damian/orca/workspaces/claude-widget/Orca","workspace":"Feat orca ide integration",
        "hostId":"local","paneKey":"structured-agent-session-a1:ec7426d2"}]},"_meta":{"runtimeId":"r"}}
        """;

    // Odpowiedź `orca terminal switch --json`, skopiowana z Orca 1.4.263.
    private const string Switched = """
        {"id":"x","ok":true,"result":{"focus":{"handle":"term_1","tabId":"t","worktreeId":"w::C:/apps/emx-monorepo","navigated":true}},"_meta":{"runtimeId":"r"}}
        """;

    [Fact]
    public async Task A_terminal_session_switches_straight_to_its_terminal()
    {
        var cli = new FakeOrcaCli().Reply("terminal switch --terminal term_1", Switched);

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace("a1", "term_1"), "C:\\apps\\api", CancellationToken.None);

        Assert.Equal(OrcaShown.Terminal, result.Shown);
        Assert.Equal(["terminal switch --terminal term_1"], cli.Calls);
    }

    [Fact]
    public async Task A_chat_opens_the_workspace_orca_reports_for_its_agent()
    {
        var cli = new FakeOrcaCli()
            .Reply("agent show a1", AgentShowChat)
            .Reply($"worktree activate --worktree id:{ChatWorkspace}", Ok);

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace("a1", null), "C:\\elsewhere", CancellationToken.None);

        Assert.Equal(OrcaShown.Workspace, result.Shown);
        Assert.Equal(["agent show a1", $"worktree activate --worktree id:{ChatWorkspace}"], cli.Calls);
    }

    [Fact]
    public async Task The_second_click_on_a_chat_reuses_its_workspace_without_asking_orca_again()
    {
        var cli = new FakeOrcaCli()
            .Reply("agent show a1", AgentShowChat)
            .Reply($"worktree activate --worktree id:{ChatWorkspace}", Ok);
        var navigator = new OrcaNavigator(cli);

        await navigator.ShowAsync(new OrcaPlace("a1", null), "", CancellationToken.None);
        cli.Calls.Clear();
        await navigator.ShowAsync(new OrcaPlace("a1", null), "", CancellationToken.None);

        Assert.Equal([$"worktree activate --worktree id:{ChatWorkspace}"], cli.Calls);
    }

    [Fact]
    public async Task A_stale_terminal_handle_falls_back_to_the_agent_workspace()
    {
        var cli = new FakeOrcaCli()
            .Reply("terminal switch --terminal term_gone", """{"ok":false,"error":{"code":"terminal_handle_stale"}}""")
            .Reply("agent show a1", AgentShowChat)
            .Reply($"worktree activate --worktree id:{ChatWorkspace}", Ok);

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace("a1", "term_gone"), "", CancellationToken.None);

        Assert.Equal(OrcaShown.Workspace, result.Shown);
        Assert.Contains("orca terminal switch: terminal_handle_stale", result.Errors);
    }

    [Fact]
    public async Task A_terminal_switch_that_did_not_move_the_window_falls_back_to_the_workspace()
    {
        var cli = new FakeOrcaCli()
            .Reply("terminal switch --terminal term_1", Switched.Replace("\"navigated\":true", "\"navigated\":false"))
            .Reply("worktree activate --worktree path:C:\\apps\\api", Ok);

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace(null, "term_1"), "C:\\apps\\api", CancellationToken.None);

        Assert.Equal(OrcaShown.Workspace, result.Shown);
    }

    [Fact]
    public async Task A_cancelled_show_runs_no_orca_command()
    {
        var cli = new FakeOrcaCli().Reply("terminal switch --terminal term_1", Switched);

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace("a1", "term_1"), "C:\\apps\\api", new CancellationToken(canceled: true));

        Assert.Equal(OrcaShown.NotFound, result.Shown);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task An_unknown_agent_falls_back_to_the_workspace_at_the_session_directory()
    {
        var cli = new FakeOrcaCli()
            .Reply("agent show a1", """{"ok":false,"error":{"code":"agent_unknown"}}""")
            .Reply("worktree activate --worktree path:C:\\apps\\api", Ok);

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace("a1", null), "C:\\apps\\api", CancellationToken.None);

        Assert.Equal(OrcaShown.Workspace, result.Shown);
        Assert.Equal(["agent show a1", "worktree activate --worktree path:C:\\apps\\api"], cli.Calls);
    }

    [Fact]
    public async Task Nothing_found_reports_every_failed_step()
    {
        var cli = new FakeOrcaCli();

        var result = await new OrcaNavigator(cli).ShowAsync(new OrcaPlace("a1", "term_1"), "C:\\apps\\api", CancellationToken.None);

        Assert.Equal(OrcaShown.NotFound, result.Shown);
        Assert.Equal(3, result.Errors.Count);
    }

    // Odpowiedź `orca agent list --json` (skrócona), skopiowana z Orca 1.4.263.
    private const string AgentList = """
        {"id":"x","ok":true,"result":{"agents":[
        {"orcaSessionId":"dfbd0007","kind":"chat","agent":"claude","state":"working","worktreeId":"w::C:/x","workspace":"Feat orca ide integration","repo":"Widget Claude","title":"Integracja widgetu z Orca IDE","lastAssistantMessage":null,"updatedAt":1},
        {"orcaSessionId":"fresh","kind":"chat","agent":"claude","state":"working","workspace":"feat/nowy","title":null},
        {"orcaSessionId":"nameless","kind":"chat","agent":"claude","state":"done"}
        ],"truncated":false},"_meta":{"runtimeId":"r"}}
        """;

    [Fact]
    public async Task Agent_titles_and_workspaces_come_from_orcas_agent_list()
    {
        var cli = new FakeOrcaCli().Reply("agent list", AgentList);

        var labels = await OrcaLabel.ListAsync(cli, CancellationToken.None);

        Assert.Equal(new OrcaLabel("Integracja widgetu z Orca IDE", "Feat orca ide integration"), labels!["dfbd0007"]);
        Assert.Equal(new OrcaLabel("feat/nowy", "feat/nowy"), labels["fresh"]);
        Assert.False(labels.ContainsKey("nameless"));
    }

    [Fact]
    public async Task A_failed_agent_list_gives_no_labels_so_the_previous_ones_stay()
    {
        Assert.Null(await OrcaLabel.ListAsync(new FakeOrcaCli(), CancellationToken.None));
    }

    [Theory]
    [InlineData("dfbd0007-1109-4766-9512-58f3bb8b4dfc", null, true)]
    [InlineData(null, "term_8edd3d90-a6fc-4baf-a3d8-97e69f3c9126", true)]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("a1 --json", null, false)]
    [InlineData(null, "term\"; calc", false)]
    [InlineData("--help", null, false)]
    [InlineData(null, "term_1\n", false)]
    public void Only_identifier_shaped_values_make_an_orca_place(string? agent, string? terminal, bool expected)
    {
        Assert.Equal(expected, OrcaPlace.From(agent, terminal) is not null);
    }

    [Fact]
    public void A_reply_without_ok_true_is_an_error_with_orcas_code()
    {
        Assert.Equal("agent_unknown", OrcaCli.Parse("""{"ok":false,"error":{"code":"agent_unknown","message":"No agent"}}""").Error);
        Assert.False(OrcaCli.Parse("Orca is not running").Ok);
        Assert.False(OrcaCli.Parse("[]").Ok);
    }

    [Fact]
    public void A_successful_reply_carries_its_result()
    {
        var reply = OrcaCli.Parse("""{"ok":true,"result":{"focused":true}}""");

        Assert.True(reply.Ok);
        Assert.True(reply.Result!.Value.GetProperty("focused").GetBoolean());
    }

    [Fact]
    public void The_cli_is_found_in_orcas_per_user_install_before_path()
    {
        var installed = Path.Combine("C:\\Users\\u\\AppData\\Local", "Programs", "orca", "resources", "bin", "orca.exe");
        var onPath = Path.Combine("C:\\tools", "orca.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { installed, onPath };

        Assert.Equal(installed, OrcaCli.Find(files.Contains, "C:\\Users\\u\\AppData\\Local", "C:\\tools")!.Executable);
        Assert.Equal(onPath, OrcaCli.Find(files.Contains, "C:\\nowhere", "C:\\bin;\"C:\\tools\"")!.Executable);
        Assert.Null(OrcaCli.Find(files.Contains, null, "C:\\bin"));
    }
}
