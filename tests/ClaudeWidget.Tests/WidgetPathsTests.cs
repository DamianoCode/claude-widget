using ClaudeWidget.Core;

namespace ClaudeWidget.Tests;

public class WidgetPathsTests
{
    [Fact]
    public void A_trailing_separator_does_not_move_the_widget_directory()
    {
        // Rozszerzenie VS Code liczy katalog mostu jako dirname(resolve(CLAUDE_WIDGET_STATE_DIR)).
        Assert.Equal(@"D:\w", new WidgetPaths(@"D:\w\state\").WidgetDir);
        Assert.Equal(new WidgetPaths(@"D:\w\state").StateDir, new WidgetPaths(@"D:\w\state\").StateDir);
    }

    [Fact]
    public void Session_file_names_keep_only_safe_characters()
    {
        var paths = new WidgetPaths(@"D:\w\state");

        Assert.Equal(@"D:\w\state\ab-c_1.seen.json", paths.SessionFile(@"a/b\..-c_1", SessionFileKind.Seen));
        Assert.Null(paths.SessionFile("../..", SessionFileKind.State));
    }
}
