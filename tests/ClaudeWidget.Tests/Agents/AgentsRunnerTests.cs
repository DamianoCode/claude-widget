using System.Text;
using ClaudeWidget.Core.Agents;

namespace ClaudeWidget.Tests.Agents;

/// <summary>Port of the real-process cases in tests/agents.test.mjs (Windows only, like the original).</summary>
public sealed class AgentsRunnerTests
{
    [Fact]
    public async Task An_npm_installed_claude_cmd_is_run_through_the_shell()
    {
        using var dir = new TempDir();
        var bin = dir.File("bin with space");
        Directory.CreateDirectory(bin);
        var listing = """[{"pid":42,"cwd":"C:/apps/api","kind":"interactive","sessionId":"5a5cb1ac-2aa8-460a-a0af-b8e980134376","name":"x","status":"busy"}]""";
        await File.WriteAllTextAsync(Path.Combine(bin, "claude.cmd"), $"@echo off\r\necho {listing}\r\n");

        var snapshot = await AgentsRunner.RunAsync([], 5000, File.Exists, bin, ".EXE;.CMD", CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal(42, snapshot.Sessions[0].Pid);
    }

    [Fact]
    public async Task Polish_letters_in_the_listing_survive_as_utf8()
    {
        using var dir = new TempDir();
        var bin = dir.File("bin");
        Directory.CreateDirectory(bin);
        // `echo` mangles some bytes going through cmd's own encoding; `type` passes the file's raw
        // bytes straight through, exactly like a real child process writing UTF-8 to its stdout pipe.
        var listing = """[{"pid":42,"cwd":"C:/apps/zażółć","kind":"interactive","sessionId":"5a5cb1ac-2aa8-460a-a0af-b8e980134376","name":"zażółć gęślą jaźń","status":"busy"}]""";
        await File.WriteAllTextAsync(Path.Combine(bin, "listing.json"), listing, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(Path.Combine(bin, "claude.cmd"), "@echo off\r\ntype \"%~dp0listing.json\"\r\n");

        var snapshot = await AgentsRunner.RunAsync([], 5000, File.Exists, bin, ".EXE;.CMD", CancellationToken.None);

        Assert.True(snapshot.Ok);
        Assert.Equal("zażółć gęślą jaźń", snapshot.Sessions[0].Name);
        Assert.Equal("C:/apps/zażółć", snapshot.Sessions[0].Cwd);
    }

    [Fact]
    public async Task Without_claude_on_path_the_list_is_switched_off()
    {
        using var dir = new TempDir();
        var snapshot = await AgentsRunner.RunAsync([], 5000, File.Exists, dir.Path, ".EXE;.CMD", CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public async Task A_listing_that_cannot_be_read_switches_the_feature_off()
    {
        using var dir = new TempDir();
        var bin = dir.File("bin");
        Directory.CreateDirectory(bin);
        await File.WriteAllTextAsync(Path.Combine(bin, "claude.cmd"), "@echo off\r\necho { not json\r\n");

        var snapshot = await AgentsRunner.RunAsync([], 5000, File.Exists, bin, ".EXE;.CMD", CancellationToken.None);

        Assert.False(snapshot.Ok);
        Assert.Empty(snapshot.Sessions);
    }
}
