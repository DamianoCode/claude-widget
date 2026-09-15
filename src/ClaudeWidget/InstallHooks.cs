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
        ReconcileSettings();
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

    /// <summary>
    /// Hooki i statusline w settings.json — po instalacji i aktualizacji, a także przy każdym starcie
    /// widżetu, bo powłoka statusline może się zmienić (ktoś zainstalował albo usunął Git Bash). Gdy nic
    /// się nie zmienia, plik zostaje nietknięty.
    /// </summary>
    public static void ReconcileSettings() => Safe("hooki i statusline", () =>
        ClaudeSettings.Install(ClaudeSettings.DefaultPath, HookExePath(), ShellSafePath(HookExePath()),
            GitBashAvailable() ? StatusLineShell.Bash : StatusLineShell.PowerShell));

    // Claude Code uruchamia statusline przez Git Bash, a bez niego przez PowerShell — obie powłoki przyjmą
    // ścieżkę bez cudzysłowu, jeśli nie ma w niej znaków specjalnych (spacja, apostrof, nawias, &…). Gdy
    // katalog je ma (np. w nazwie użytkownika), bierze się jego krótką nazwę 8.3; nazwa pliku zostaje,
    // bo po niej instalator rozpoznaje swoje wpisy.
    private static string ShellSafePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (ClaudeSettings.IsShellSafe(dir)) return path;
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetShortPathName(dir, buffer, buffer.Capacity);
        var shortDir = buffer.ToString();
        return length > 0 && length < buffer.Capacity && ClaudeSettings.IsShellSafe(shortDir) ? Path.Combine(shortDir, Path.GetFileName(path)) : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int size);

    // Czy Claude Code uruchomi statusline przez Git Bash (tak jak on: własna ścieżka albo Git for Windows).
    // Tylko wtedy wpina się przekaźnik przed cudzą statusline.
    private static bool GitBashAvailable()
    {
        var custom = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        if (!string.IsNullOrWhiteSpace(custom)) return File.Exists(custom.Trim('"'));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var git = Path.Combine(dir.Trim().Trim('"'), "git.exe");
            if (!File.Exists(git)) continue;
            var root = Path.GetDirectoryName(Path.GetDirectoryName(git));
            if (root is not null && File.Exists(Path.Combine(root, "bin", "bash.exe"))) return true;
        }
        return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"));
    }

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
