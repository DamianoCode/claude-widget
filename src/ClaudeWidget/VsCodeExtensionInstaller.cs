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
        // Znacznik trzyma wersję osobno dla każdego edytora: zepsuty jeden (np. nieaktualny wpis
        // w PATH) nie może wymuszać ponownej instalacji we wszystkich przy każdym starcie. Edytor
        // zainstalowany po widżecie dostaje rozszerzenie przy następnym starcie widżetu.
        var marker = MarkerPath(paths);
        var installed = ReadMarker(marker);
        var changed = false;
        foreach (var cli in FindClis())
        {
            var key = Path.GetFullPath(cli);
            if (installed.TryGetValue(key, out var installedVersion) && installedVersion == version) continue;
            if (!Run(cli, $"--install-extension \"{VsixPath}\" --force", log, TimeSpan.FromMinutes(2))) continue;
            installed[key] = version;
            changed = true;
        }
        if (!changed) return;
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllLines(marker, installed.Select(entry => $"{entry.Value}\t{entry.Key}"));
    }

    public static void Uninstall(WidgetPaths paths, Action<string> log)
    {
        // Wszystkie edytory naraz i jeden wspólny limit — hook odinstalowania Velopacka ma 30 s.
        var runs = FindClis()
            .Select(cli => Task.Run(() => Run(cli, $"--uninstall-extension {ExtensionId}", log, TimeSpan.FromSeconds(20))))
            .ToArray();
        Task.WaitAll(runs, TimeSpan.FromSeconds(22));
        JsonStore.Remove(MarkerPath(paths));
    }

    private static string MarkerPath(WidgetPaths paths) => Path.Combine(paths.WidgetDir, "vscode", "installed-extension.txt");

    // Wiersze „wersja<TAB>ścieżka CLI”; wiersze w innym formacie (np. z wcześniejszej wersji) się pomija.
    private static Dictionary<string, string> ReadMarker(string marker)
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(marker)) return installed;
        foreach (var line in File.ReadAllLines(marker))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0) installed[parts[1]] = parts[0];
        }
        return installed;
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
