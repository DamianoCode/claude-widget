using System.Text.Json;
using ClaudeWidget.Core.Ide;

namespace ClaudeWidget.Tests.Ide;

public class VsCodeBridgeTests
{
    // Kształt, który zapisuje extension/src/extension.ts.
    private const string WindowJson = """
        {"version":1,"extensionHostPid":5000,"workspaceFolders":["c:\\apps\\emx-monorepo","c:\\apps\\emx-monorepo\\web"],
         "focused":true,"activeTerminalPid":4120,"terminalPids":[200,4120],"updatedAt":1789456714293}
        """;

    private static readonly int[] Chain = [9001, 4120, 3300, 12780];

    [Fact]
    public void Windows_written_by_the_extension_are_read_and_dead_ones_are_cleaned_up()
    {
        using var dir = new TempDir();
        var windows = Path.Combine(dir.Path, "windows");
        Directory.CreateDirectory(windows);
        File.WriteAllText(Path.Combine(windows, "live.json"), WindowJson);
        File.WriteAllText(Path.Combine(windows, "dead.json"), WindowJson.Replace("5000", "6000"));
        File.WriteAllText(Path.Combine(windows, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(windows, "live.json.5000.tmp"), WindowJson);

        var bridge = new VsCodeBridge(dir.Path, pid => pid == 5000);
        var read = bridge.ReadWindows();

        var window = Assert.Single(read);
        Assert.Equal(5000, window.ExtensionHostPid);
        Assert.True(window.Focused);
        Assert.Equal(4120, window.ActiveTerminalPid);
        Assert.Equal([200, 4120], window.TerminalPids);
        Assert.False(File.Exists(Path.Combine(windows, "dead.json")), "the file of a closed window is removed");
        Assert.Empty(new VsCodeBridge(dir.File("missing"), _ => true).ReadWindows());
    }

    [Fact]
    public void The_window_owning_the_session_terminal_is_found_and_looking_needs_focus_and_the_active_terminal()
    {
        var window = JsonSerializer.Deserialize(WindowJson, BridgeJson.Default.VsCodeWindow)!;
        var other = window with { TerminalPids = [77], ActiveTerminalPid = 77 };

        Assert.Same(window, VsCodeBridge.OwnerOf([other, window], Chain));
        Assert.Null(VsCodeBridge.OwnerOf([other], Chain));
        Assert.True(VsCodeBridge.IsLookingAt(window, Chain));
        Assert.False(VsCodeBridge.IsLookingAt(window with { Focused = false }, Chain));
        Assert.False(VsCodeBridge.IsLookingAt(window with { ActiveTerminalPid = 200 }, Chain));
        Assert.False(VsCodeBridge.IsLookingAt(window with { ActiveTerminalPid = null }, Chain));
    }

    [Fact]
    public void The_title_hint_is_the_deepest_window_folder_holding_the_session_or_the_first_one()
    {
        var window = JsonSerializer.Deserialize(WindowJson, BridgeJson.Default.VsCodeWindow)!;

        Assert.Equal("web", VsCodeBridge.FolderLeaf(window, @"C:\apps\emx-monorepo\web\src"));
        Assert.Equal("emx-monorepo", VsCodeBridge.FolderLeaf(window, @"D:\elsewhere"));
        Assert.Null(VsCodeBridge.FolderLeaf(window with { WorkspaceFolders = [] }, @"C:\x"));
    }

    [Fact]
    public void A_focus_request_has_the_shape_the_extension_reads()
    {
        using var dir = new TempDir();
        var bridge = new VsCodeBridge(dir.Path, _ => true);

        bridge.RequestFocus(Chain, 1789456714293);

        using var request = JsonDocument.Parse(File.ReadAllText(bridge.RequestFile));
        var root = request.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(32, root.GetProperty("id").GetString()!.Length);
        Assert.Equal(Chain, root.GetProperty("pids").EnumerateArray().Select(pid => pid.GetInt32()));
        Assert.Equal(1789456714293, root.GetProperty("at").GetInt64());
    }
}
