using System.Text.Json;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Hook;
using ClaudeWidget.Core.Settings;
using ClaudeWidget.Core.StatusLine;

namespace ClaudeWidget.Tests.Settings;

// Zgodność z wersją z Node tam, gdzie port się od niej rozjechał.
public class ClaudeSettingsFormatTests
{
    private const string HookExe = @"C:\Users\me\AppData\Local\ClaudeWidget\current\ClaudeWidgetHook.exe";

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void The_settings_file_stays_as_readable_as_JSON_stringify_output()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{"env":{"AUTHOR":"Łukasz"},"hooks":{"Stop":[{"hooks":[{"type":"command","command":"a && b"}]}]}}""");

        ClaudeSettings.Install(path, HookExe);

        var text = File.ReadAllText(path);
        Assert.StartsWith("{\n  \"env\": {\n    \"AUTHOR\": \"Łukasz\"", text);
        Assert.Contains("\"command\": \"a && b\"", text);
        Assert.Contains("\"command\": \"\\\"C:/Users/me/AppData/Local/ClaudeWidget/current/ClaudeWidgetHook.exe\\\" statusline\"", text);
        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("}\n", text);
    }

    [Fact]
    public void A_settings_file_with_a_duplicate_key_is_left_alone_with_a_readable_error()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        const string original = """{"a":1,"a":2}""";
        File.WriteAllText(path, original);

        var error = Assert.Throws<InvalidOperationException>(() => ClaudeSettings.Install(path, HookExe));

        Assert.Contains("Niczego nie zmieniono", error.Message);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-widget-*"));
    }

    [Fact]
    public void A_tool_input_of_another_shape_does_not_break_the_permission_check()
    {
        var waiting = new SessionState
        {
            State = SessionStates.Waiting,
            Since = 1,
            TurnStartedAt = 1,
            PendingTool = Json("""{"name":"Bash","input":{"command":"npm test"}}"""),
            UpdatedAt = 1,
        };

        var next = HookEngine.Transition("PostToolUse", Json("""{"hook_event_name":"PostToolUse","tool_name":"Bash","tool_input":["npm test"]}"""), waiting, 5);

        Assert.Null(next);
    }

    [Fact]
    public void A_project_dir_with_a_trailing_separator_still_names_the_project()
    {
        using var dir = new TempDir();
        var paths = new WidgetPaths(dir.Path);

        StatusLineEngine.Record(paths, Json("""{"session_id":"s","workspace":{"project_dir":"C:\\apps\\proj\\"}}"""), 7);

        var usage = JsonStore.Read(paths.SessionFile("s", SessionFileKind.Usage)!, StateJson.Default.SessionUsage);
        Assert.Equal("proj", usage!.Project);
    }
}
