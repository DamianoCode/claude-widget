using ClaudeWidget.Core;

namespace ClaudeWidget;

/// <summary>Argumenty startowe widżetu. <c>--state-dir</c> uruchamia osobną instancję (np. do testów).</summary>
public sealed record StartupOptions(string StateDir)
{
    public static StartupOptions Parse(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--state-dir") return new StartupOptions(args[i + 1]);
        }
        return new StartupOptions(WidgetPaths.FromEnvironment().StateDir);
    }
}
