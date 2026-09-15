using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using ClaudeWidget.Core;

namespace ClaudeWidget;

/// <summary>
/// Rozszerzenie „Claude Code widget bridge” jedzie w paczce widżetu (ClaudeWidgetBridge.vsix obok
/// exe). Widżet instaluje je sam przy starcie — w każdym znalezionym VS Code i jego odmianach — raz
/// na wersję widżetu. W hooku instalatora nie, bo ten ma 15–30 s, a `code --install-extension`
/// potrafi trwać dłużej.
/// </summary>
public static class VsCodeExtensionInstaller
{
    public const string ExtensionId = "damianocode.claude-widget-bridge";

    private static readonly string[] CliNames = ["code.cmd", "code-insiders.cmd", "cursor.cmd", "windsurf.cmd"];

    private static string VsixPath => Path.Combine(AppContext.BaseDirectory, "ClaudeWidgetBridge.vsix");

    public static void InstallIfNeeded(WidgetPaths paths, Action<string> log)
    {
        if (!File.Exists(VsixPath)) return; // uruchomienie deweloperskie — paczki nie ma
        var version = typeof(VsCodeExtensionInstaller).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        var marker = Path.Combine(paths.WidgetDir, "vscode", "installed-extension.txt");
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;

        // Bez VS Code nie zapisuje się znacznika: gdy ktoś zainstaluje je później, rozszerzenie
        // dojdzie przy następnym starcie widżetu.
        var clis = FindClis().ToList();
        if (clis.Count == 0) return;

        var installed = true;
        foreach (var cli in clis)
        {
            installed &= Run(cli, $"--install-extension \"{VsixPath}\" --force", log, TimeSpan.FromMinutes(2));
        }
        if (!installed) return;
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, version);
    }

    public static void Uninstall(Action<string> log)
    {
        foreach (var cli in FindClis())
        {
            Run(cli, $"--uninstall-extension {ExtensionId}", log, TimeSpan.FromSeconds(12));
        }
    }

    // CLI z PATH i z domyślnych miejsc instalacji VS Code (instalacja użytkownika i systemowa).
    private static IEnumerable<string> FindClis()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => dir.Trim().Trim('"'))
            .Concat([
                Path.Combine(local, "Programs", "Microsoft VS Code", "bin"),
                Path.Combine(programFiles, "Microsoft VS Code", "bin"),
            ]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            foreach (var name in CliNames)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate))) yield return candidate;
            }
        }
    }

    private static bool Run(string cli, string arguments, Action<string> log, TimeSpan timeout)
    {
        var name = Path.GetFileName(cli);
        var info = new ProcessStartInfo("cmd.exe")
        {
            // Plik .cmd uruchomi tylko cmd.exe; całe polecenie w cudzysłowie, ścieżka też.
            Arguments = $"/d /s /c \"\"{cli}\" {arguments}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                log($"rozszerzenie VS Code ({name}): przekroczony czas");
                return false;
            }
            if (process.ExitCode != 0)
            {
                log($"rozszerzenie VS Code ({name}): kod {process.ExitCode} {error.Result.Trim()} {output.Result.Trim()}");
                return false;
            }
            log($"rozszerzenie VS Code ({name}): {arguments.Split(' ')[0].TrimStart('-')} OK");
            return true;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException)
        {
            log($"rozszerzenie VS Code ({name}): {failure.Message}");
            return false;
        }
    }
}
