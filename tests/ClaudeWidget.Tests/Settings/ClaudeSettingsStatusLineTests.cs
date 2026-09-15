using System.Text.Json.Nodes;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget.Tests.Settings;

// Cudza statusline: przekaźnik widżetu przed prostą komendą, nietknięta złożona, powrót do oryginału.
public class ClaudeSettingsStatusLineTests
{
    private const string HookExe = @"C:\Users\me\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe";
    private const string Exe = "C:/Users/me/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe";

    private static JsonObject WithStatusLine(string command) =>
        new() { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command, ["padding"] = 1 } };

    private static string? CommandOf(JsonObject settings) => (string?)settings["statusLine"]!["command"];

    [Fact]
    public void A_simple_foreign_status_line_gets_the_relay_in_front_and_keeps_its_options()
    {
        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine("bash ~/.claude/statusline.sh"), HookExe, relayForeign: true);

        Assert.False(foreign);
        Assert.Equal($"{Exe} tee | bash ~/.claude/statusline.sh", CommandOf(settings));
        Assert.Equal(1, (int)settings["statusLine"]!["padding"]!);
    }

    [Theory]
    [InlineData("bash a.sh && echo done")]
    [InlineData("node a.js | head -1")]
    [InlineData("echo $(date)")]
    public void A_compound_foreign_status_line_is_left_alone(string command)
    {
        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine(command), HookExe, relayForeign: true);

        Assert.True(foreign);
        Assert.Equal(command, CommandOf(settings));
    }

    [Fact]
    public void Without_Git_Bash_a_foreign_status_line_is_left_alone()
    {
        var (settings, foreign) = ClaudeSettings.WithWidget(WithStatusLine("my-status.sh"), HookExe, relayForeign: false);

        Assert.True(foreign);
        Assert.Equal("my-status.sh", CommandOf(settings));
    }

    [Fact]
    public void Reinstalling_does_not_stack_relays_and_uninstalling_restores_the_original_command()
    {
        var (once, _) = ClaudeSettings.WithWidget(WithStatusLine("bash ~/.claude/statusline.sh"), HookExe, relayForeign: true);
        var (twice, _) = ClaudeSettings.WithWidget(once, HookExe, relayForeign: true);
        Assert.Equal(CommandOf(once), CommandOf(twice));

        var removed = ClaudeSettings.WithoutWidget(twice);
        Assert.Equal("bash ~/.claude/statusline.sh", CommandOf(removed));
        Assert.Equal(1, (int)removed["statusLine"]!["padding"]!);
    }

    [Fact]
    public void A_path_with_spaces_is_quoted_unless_a_space_free_path_is_given()
    {
        const string spaced = @"C:\Users\Jan Kowalski\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe";

        var (quoted, _) = ClaudeSettings.WithWidget(new JsonObject(), spaced);
        Assert.Equal("\"C:/Users/Jan Kowalski/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe\" statusline", CommandOf(quoted));

        var (shortened, _) = ClaudeSettings.WithWidget(new JsonObject(), spaced, @"C:\Users\JANKOW~1\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe");
        Assert.Equal("C:/Users/JANKOW~1/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe statusline", CommandOf(shortened));
        Assert.Null(ClaudeSettings.WithoutWidget(shortened)["statusLine"]);
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
        ClaudeSettings.Install(path, HookExe, relayForeign: true);
        Assert.Equal(StatusLineKind.Relayed, ClaudeSettings.Inspect(path));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(StatusLineKind.Foreign, ClaudeSettings.Inspect(path));
    }
}
