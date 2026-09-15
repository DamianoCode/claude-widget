using System.IO;
using Microsoft.Win32;

namespace ClaudeWidget;

/// <summary>
/// Autostart przez HKCU\...\Run zamiast skrótu w Autostart (jak dawny widget.ps1) — Velopack
/// instaluje w katalogu z numerem wersji, więc wpis musi wskazywać na stabilny launcher jeden
/// poziom nad "current\", nie na bieżący ClaudeWidget.exe.
/// </summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeCodeWidget";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{StableLauncherPath()}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Zainstalowany przez Velopack: exe uruchamiającego jeden katalog nad "...\current\", które
    /// samo znajduje najnowszą wersję. Uruchomiony spoza instalacji (dev, testy): bieżący exe.
    /// </summary>
    public static string StableLauncherPath()
    {
        var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var directory = Path.GetDirectoryName(exePath) ?? "";
        if (Path.GetFileName(directory).Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            var appDir = Path.GetDirectoryName(directory) ?? directory;
            var launcher = Path.Combine(appDir, Path.GetFileName(exePath));
            if (File.Exists(launcher)) return launcher;
        }
        return exePath;
    }
}
