using System.IO;
using System.Management;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget;

/// <summary>
/// Wywoływane przez VelopackApp po instalacji/aktualizacji i przed odinstalowaniem. Hooki nie mogą
/// pokazywać UI i mają 15-30 s, więc wszystko tu jest krótkie i owinięte w try/catch — błąd instalacji
/// hooków nie może wywalić instalatora.
/// </summary>
public static class InstallHooks
{
    private static readonly string[] LegacyScriptFiles =
        ["hook.mjs", "statusline.mjs", "store.mjs", "agents.mjs", "widget.ps1", "start-widget.vbs"];

    public static void AfterInstallOrUpdate(bool isFirstInstall)
    {
        Log("po instalacji/aktualizacji");
        Safe("hooki", () => ClaudeSettings.Install(ClaudeSettings.DefaultPath, HookExePath()));
        Safe("sprzątanie starej wersji", CleanupLegacyInstall);
        if (isFirstInstall)
        {
            Safe("autostart", () => AutostartService.SetEnabled(true));
        }
    }

    public static void BeforeUninstall()
    {
        Log("przed odinstalowaniem");
        Safe("usuwanie hooków", () => ClaudeSettings.Remove(ClaudeSettings.DefaultPath));
        Safe("autostart", () => AutostartService.SetEnabled(false));
        Safe("rozszerzenie VS Code", () => VsCodeExtensionInstaller.Uninstall(WidgetPaths.FromEnvironment(), Log));
    }

    private static string HookExePath() => Path.Combine(AppContext.BaseDirectory, "ClaudeWidgetHook.exe");

    // Sprzątanie po instalatorze z wersji PowerShell: zatrzymuje jej proces, usuwa skrót
    // w Autostart i stare pliki skryptów. Nigdy nie rusza katalogu stanu, configu ani logu —
    // to one niosą otwarte sesje przez aktualizację.
    private static void CleanupLegacyInstall()
    {
        StopLegacyWidgetProcesses();

        var startupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Claude Code widget.lnk");
        Safe("stary skrót autostartu", () => { if (File.Exists(startupLink)) File.Delete(startupLink); });

        var widgetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "widget");
        foreach (var name in LegacyScriptFiles)
        {
            var path = Path.Combine(widgetDir, name);
            Safe($"stary plik {name}", () => { if (File.Exists(path)) File.Delete(path); });
        }
    }

    private static void StopLegacyWidgetProcesses()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe'");
        using var results = searcher.Get();
        foreach (ManagementObject process in results)
        {
            var commandLine = (string?)process["CommandLine"] ?? "";
            if (!commandLine.Contains(@"\.claude\widget\widget.ps1", StringComparison.OrdinalIgnoreCase)) continue;
            var pid = (uint)process["ProcessId"];
            Safe($"zatrzymanie starego widżetu (pid {pid})", () => System.Diagnostics.Process.GetProcessById((int)pid).Kill());
        }
    }

    private static void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception error) { Log($"{what}: {error}"); }
    }

    private static void Log(string message)
    {
        try
        {
            var paths = WidgetPaths.FromEnvironment();
            Directory.CreateDirectory(paths.WidgetDir);
            File.AppendAllText(paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} instalator: {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // hooki nie mogą pokazać UI; bez logu ta jedna wiadomość po prostu ginie
        }
    }
}
