using System.Text.Json.Nodes;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget.Tests.Settings;

// Cudza statusline: przekaźnik widżetu przed prostą komendą, nietknięta złożona, powrót do oryginału;
// zapis komendy zależny od powłoki, w której Claude Code uruchomi statusline.
public class ClaudeSettingsStatusLineTests
{
    private const string HookExe = @"C:\Users\me\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe";
    private const string Relay = "C:/Users/me/AppData/Local/ClaudeWidget/current/ClaudeWidgetRelay.exe";

    private static JsonObject WithStatusLine(string command) =>
        new() { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command, ["padding"] = 1 } };

    private static string? CommandOf(JsonObject settings) => (string?)settings["statusLine"]!["command"];

    [Fact]
    public void A_simple_foreign_status_line_gets_a_fail_open_relay_in_front_and_keeps_its_options()
    {
        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine("bash ~/.claude/statusline.sh"), HookExe, shell: StatusLineShell.Bash);

        Assert.False(foreign);
        Assert.Equal($"{{ {Relay} tee || cat; }} 2>/dev/null | bash ~/.claude/statusline.sh", CommandOf(settings));
        Assert.Equal(1, (int)settings["statusLine"]!["padding"]!);
    }

    [Theory]
    [InlineData("bash a.sh && echo done")]
    [InlineData("node a.js | head -1")]
    [InlineData("echo $(date)")]
    public void A_compound_foreign_status_line_is_left_alone(string command)
    {
        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine(command), HookExe, shell: StatusLineShell.Bash);

        Assert.True(foreign);
        Assert.Equal(command, CommandOf(settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(StatusLineShell.PowerShell)]
    public void Without_Git_Bash_a_foreign_status_line_is_left_alone(StatusLineShell? shell)
    {
        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine("my-status.sh"), HookExe, shell: shell);

        Assert.True(foreign);
        Assert.Equal("my-status.sh", CommandOf(settings));
    }

    [Fact]
    public void Reinstalling_does_not_stack_relays_and_uninstalling_restores_the_original_command()
    {
        var (once, _) = ClaudeSettings.WithWidget(WithStatusLine("bash ~/.claude/statusline.sh"), HookExe, shell: StatusLineShell.Bash);
        var (twice, _) = ClaudeSettings.WithWidget(once, HookExe, shell: StatusLineShell.Bash);
        Assert.Equal(CommandOf(once), CommandOf(twice));

        var removed = ClaudeSettings.WithoutWidget(twice);
        Assert.Equal("bash ~/.claude/statusline.sh", CommandOf(removed));
        Assert.Equal(1, (int)removed["statusLine"]!["padding"]!);
    }

    [Fact]
    public void When_Git_Bash_disappears_the_relay_is_taken_off_again()
    {
        var (relayed, _) = ClaudeSettings.WithWidget(WithStatusLine("bash ~/.claude/statusline.sh"), HookExe, shell: StatusLineShell.Bash);

        var (powershell, foreign) = ClaudeSettings.WithWidget(relayed, HookExe, shell: StatusLineShell.PowerShell);

        Assert.True(foreign);
        Assert.Equal("bash ~/.claude/statusline.sh", CommandOf(powershell));
    }

    [Theory]
    [InlineData(@"C:\Users\Jan Kowalski\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe", StatusLineShell.Bash, "\"C:/Users/Jan Kowalski/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe\" statusline")]
    [InlineData(@"C:\Users\Jan Kowalski\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe", StatusLineShell.PowerShell, "& 'C:/Users/Jan Kowalski/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe' statusline")]
    [InlineData(@"C:\Users\O'Brien\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe", StatusLineShell.Bash, "\"C:/Users/O'Brien/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe\" statusline")]
    [InlineData(@"C:\Users\O'Brien\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe", StatusLineShell.PowerShell, "& 'C:/Users/O''Brien/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe' statusline")]
    [InlineData(@"C:\Users\JANKOW~1\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe", StatusLineShell.PowerShell, "C:/Users/JANKOW~1/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe statusline")]
    public void A_path_with_special_characters_is_written_the_way_the_shell_needs_it(string exe, StatusLineShell shell, string expected)
    {
        var (settings, _) = ClaudeSettings.WithWidget(new JsonObject(), exe, shell: shell);

        Assert.Equal(expected, CommandOf(settings));
        Assert.Null(ClaudeSettings.WithoutWidget(settings)["statusLine"]);
    }

    [Fact]
    public void No_relay_is_written_when_its_path_has_special_characters()
    {
        const string exe = @"C:\Users\Tom&Jerry\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe";

        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine("my-status.sh"), exe, shell: StatusLineShell.Bash);

        Assert.True(foreign);
        Assert.Equal("my-status.sh", CommandOf(settings));
    }

    [Fact]
    public void Installing_again_without_changes_leaves_the_file_and_makes_no_new_backup()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        ClaudeSettings.Install(path, HookExe, shell: StatusLineShell.Bash);
        var text = File.ReadAllText(path);

        ClaudeSettings.Install(path, HookExe, shell: StatusLineShell.Bash);

        Assert.Equal(text, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-widget-*"));
    }

    [Fact]
    public void Inspect_tells_whose_status_line_is_configured()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");

        Assert.Equal(StatusLineKind.Missing, ClaudeSettings.Inspect(path));
        ClaudeSettings.Install(path, HookExe);
        Assert.Equal(StatusLineKind.Widget, ClaudeSettings.Inspect(path));

        File.WriteAllText(path, WithStatusLine("my-status.sh").ToJsonString());
        Assert.Equal(StatusLineKind.Foreign, ClaudeSettings.Inspect(path));
        ClaudeSettings.Install(path, HookExe, shell: StatusLineShell.Bash);
        Assert.Equal(StatusLineKind.Relayed, ClaudeSettings.Inspect(path));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(StatusLineKind.Foreign, ClaudeSettings.Inspect(path));
    }
}
