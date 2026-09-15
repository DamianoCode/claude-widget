using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget.Tests.Settings;

// Port of tests/settings.test.mjs. The Node version pointed hooks at `node hook.mjs`; the ported
// contract points them straight at the published exe (see CONTRACT in the task), so the shapes
// asserted here differ from the .mjs source on that one point while the behavior stays the same:
// exactly the widget's entries are added/removed, never duplicated, nothing else touched.
public sealed class ClaudeSettingsTests
{
    private const string WidgetDir = "C:\\Users\\someone\\.claude\\widget";
    private const string HookExePath = "C:\\Users\\someone\\.claude\\widget\\ClaudeWidgetHook.exe";

    private static JsonObject NotifyHook(string kind) => new()
    {
        ["type"] = "command",
        ["command"] = "powershell.exe",
        ["args"] = new JsonArray("-NoProfile", "-File", "C:\\Users\\someone\\.claude\\hooks\\notify.ps1", "-Kind", kind),
        ["async"] = true,
    };

    private static JsonObject LegacyWidgetHook() => new()
    {
        ["type"] = "command",
        ["command"] = "node",
        ["args"] = new JsonArray($"{WidgetDir}\\hook.mjs"),
        ["timeout"] = 5,
    };

    // The shape this machine had before the installer existed: widget hooks sharing a group with
    // the user's own notification hook.
    private static JsonObject HandMade() => new()
    {
        ["hooks"] = new JsonObject
        {
            ["Stop"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(NotifyHook("done"), LegacyWidgetHook()) }),
            ["PreToolUse"] = new JsonArray(new JsonObject
            {
                ["matcher"] = "Bash",
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "guard.sh" }),
            }),
        },
        ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "node \"C:/Users/someone/.claude/widget/statusline.mjs\"" },
        ["theme"] = "dark",
    };

    private static List<JsonNode?> WidgetHooksIn(JsonObject settings, string eventName)
    {
        var found = new List<JsonNode?>();
        if (settings["hooks"]?[eventName] is not JsonArray groups) return found;
        foreach (var group in groups)
        {
            if (group?["hooks"] is not JsonArray hooks) continue;
            found.AddRange(hooks.Where(ClaudeSettings.IsWidgetHook));
        }
        return found;
    }

    [Fact]
    public void Installs_one_widget_hook_per_event_and_the_status_line()
    {
        var (settings, foreignStatusLine) = ClaudeSettings.WithWidget(new JsonObject { ["theme"] = "dark" }, HookExePath);
        Assert.False(foreignStatusLine);
        foreach (var spec in HookEvents.All)
        {
            var hooks = WidgetHooksIn(settings, spec.Name);
            Assert.True(hooks.Count == 1, spec.Name);
            var entry = (JsonObject)hooks[0]!;
            Assert.Equal(new[] { "hook" }, entry["args"]!.AsArray().Select(a => (string)a!));
            Assert.Equal(HookExePath, (string?)entry["command"]);
        }
        Assert.Equal($"\"{HookExePath.Replace('\\', '/')}\" statusline", (string?)settings["statusLine"]!["command"]);
        Assert.Equal("dark", (string?)settings["theme"]);
    }

    [Fact]
    public void Installing_twice_changes_nothing_the_second_time()
    {
        var once = ClaudeSettings.WithWidget(HandMade(), HookExePath).Settings;
        var twice = ClaudeSettings.WithWidget(once, HookExePath).Settings;
        Assert.True(JsonNode.DeepEquals(once, twice));
        Assert.Single(WidgetHooksIn(twice, "Stop"));
    }

    [Fact]
    public void Keeps_the_user_own_hooks_including_those_that_shared_a_group_with_the_widget()
    {
        var (settings, _) = ClaudeSettings.WithWidget(HandMade(), HookExePath);
        var stopHooks = ((JsonArray)settings["hooks"]!["Stop"]!).SelectMany(group => (JsonArray)group!["hooks"]!).ToList();
        Assert.Contains(stopHooks, hook => (hook as JsonObject)?["args"] is JsonArray args && args.Any(a => (string?)a == "done"));
        Assert.True(JsonNode.DeepEquals(settings["hooks"]!["PreToolUse"], HandMade()["hooks"]!["PreToolUse"]));
    }

    [Fact]
    public void Never_overwrites_a_status_line_that_is_not_the_widget_one()
    {
        var custom = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "my-status.sh" } };
        var expectedStatusLine = custom["statusLine"]!.DeepClone();
        var (settings, foreignStatusLine) = ClaudeSettings.WithWidget(custom, HookExePath);
        Assert.True(foreignStatusLine);
        Assert.True(JsonNode.DeepEquals(settings["statusLine"], expectedStatusLine));
    }

    [Fact]
    public void Uninstalling_removes_only_the_widget_down_to_empty_groups_and_an_empty_hooks_object()
    {
        var installed = ClaudeSettings.WithWidget(HandMade(), HookExePath).Settings;
        var removed = ClaudeSettings.WithoutWidget(installed);

        var expectedStop = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(NotifyHook("done")) });
        Assert.True(JsonNode.DeepEquals(removed["hooks"]!["Stop"], expectedStop));
        Assert.True(JsonNode.DeepEquals(removed["hooks"]!["PreToolUse"], HandMade()["hooks"]!["PreToolUse"]));
        Assert.Null(removed["statusLine"]);
        Assert.Equal("dark", (string?)removed["theme"]);
        foreach (var spec in HookEvents.All) Assert.Empty(WidgetHooksIn(removed, spec.Name));

        var emptyRoundTrip = ClaudeSettings.WithoutWidget(ClaudeSettings.WithWidget(new JsonObject(), HookExePath).Settings);
        Assert.True(JsonNode.DeepEquals(emptyRoundTrip, new JsonObject()));
    }

    [Fact]
    public void The_command_line_edits_the_file_keeps_a_backup_and_reverts_cleanly()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var original = new JsonObject
        {
            ["theme"] = "dark",
            ["hooks"] = new JsonObject { ["Stop"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(NotifyHook("done")) }) },
        };
        var originalText = original.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, "\uFEFF" + originalText); // a BOM some editors add

        ClaudeSettings.Install(path, HookExePath);
        var installed = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Single(WidgetHooksIn(installed, "PermissionRequest"));
        Assert.Contains(Directory.GetFiles(dir.Path), f => Path.GetFileName(f).StartsWith("settings.json.bak-widget-", StringComparison.Ordinal));

        ClaudeSettings.Remove(path);
        var reverted = JsonNode.Parse(File.ReadAllText(path));
        Assert.True(JsonNode.DeepEquals(reverted, JsonNode.Parse(originalText)));
    }

    [Fact]
    public void An_unreadable_settings_file_is_left_alone()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ \"theme\": \"dark\", ");

        Assert.Throws<InvalidOperationException>(() => ClaudeSettings.Install(path, HookExePath));

        Assert.Equal("{ \"theme\": \"dark\", ", File.ReadAllText(path));
        var names = Directory.GetFiles(dir.Path).Select(Path.GetFileName).Cast<string>().ToArray();
        Assert.Equal(["settings.json"], names); // no backup, no write
    }
}
