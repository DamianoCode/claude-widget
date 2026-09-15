using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget.Tests.Sessions;

public class WindowTitleHintsTests
{
    private const int VsCode = 12780;

    private static SessionInfo Session(string cwd, string name = "sesja-1c", string project = "packages") => new()
    {
        Id = "s1",
        Kind = SessionKinds.Working,
        Project = project,
        Name = name,
        Cwd = cwd,
    };

    private static readonly IdeWorkspace[] Workspaces =
    [
        new(VsCode, [@"c:\apps\emx-worktrees\it-858-kasa"]),
        new(VsCode, [@"c:\apps\emx-monorepo"]),
        new(4242, [@"C:\apps\other"]),
    ];

    [Fact]
    public void The_workspace_folder_holding_the_session_comes_first_for_a_VS_Code_window()
    {
        var hints = WindowTitleHints.For(Session(@"C:\apps\emx-monorepo\packages\api"), Workspaces, VsCode);

        Assert.Equal(["emx-monorepo", "sesja-1c", "packages"], hints);
    }

    [Fact]
    public void Folders_of_another_process_or_a_sibling_with_the_same_prefix_do_not_count()
    {
        Assert.Equal(["sesja-1c", "packages"], WindowTitleHints.For(Session(@"C:\apps\other\x"), Workspaces, VsCode));
        Assert.Equal(["sesja-1c", "packages"], WindowTitleHints.For(Session(@"C:\apps\emx-monorepo2"), Workspaces, VsCode));
    }

    [Fact]
    public void The_deepest_folder_wins_and_slashes_or_case_do_not_matter()
    {
        IdeWorkspace[] nested = [new(VsCode, [@"C:\apps\repo", @"C:\apps\repo\web"])];

        var hints = WindowTitleHints.For(Session("c:/APPS/repo/web/src/"), nested, VsCode);

        Assert.Equal("web", hints[0]);
    }

    [Fact]
    public void Hints_are_neither_empty_nor_repeated()
    {
        var hints = WindowTitleHints.For(Session("", name: "api", project: "API"), Workspaces, VsCode);

        Assert.Equal(["api"], hints);
    }

    [Fact]
    public void A_title_matches_any_hint_regardless_of_case()
    {
        Assert.True(WindowTitleHints.MatchesAny(".env.local - IT-858-kasa - Visual Studio Code", ["it-858-kasa"]));
        Assert.False(WindowTitleHints.MatchesAny("README.md - emx-monorepo - Visual Studio Code", ["it-858-kasa"]));
    }

    [Fact]
    public void Lock_files_give_the_IDE_process_and_its_folders_and_broken_ones_are_skipped()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("16654.lock"), """{"pid":12780,"workspaceFolders":["c:\\apps\\it-858-kasa"],"ideName":"Visual Studio Code","transport":"ws","authToken":"secret"}""");
        File.WriteAllText(dir.File("41960.lock"), "{ not json");
        File.WriteAllText(dir.File("1.lock"), """{"workspaceFolders":["c:\\x"]}""");
        File.WriteAllText(dir.File("notes.txt"), """{"pid":1,"workspaceFolders":[]}""");

        var workspaces = IdeLocks.Read(dir.Path);

        var only = Assert.Single(workspaces);
        Assert.Equal(12780, only.Pid);
        Assert.Equal([@"c:\apps\it-858-kasa"], only.Folders);
        Assert.Empty(IdeLocks.Read(dir.File("missing")));
    }
}
