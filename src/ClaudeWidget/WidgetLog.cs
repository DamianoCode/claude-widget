using System.IO;
using ClaudeWidget.Core;

namespace ClaudeWidget;

/// <summary>widget.log — port Write-WidgetLog. Błąd zapisu nie może wywalić widżetu.</summary>
public static class WidgetLog
{
    public static void Write(WidgetPaths paths, string message)
    {
        try
        {
            Directory.CreateDirectory(paths.WidgetDir);
            File.AppendAllText(paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // log jest tylko pomocą diagnostyczną; brak miejsca na dysku nie może zatrzymać widżetu
        }
    }
}
