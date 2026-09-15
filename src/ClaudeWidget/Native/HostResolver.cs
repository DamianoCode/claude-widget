using System.Diagnostics;
using System.Management;

namespace ClaudeWidget.Native;

/// <summary>
/// Proces z oknem, w którym działa sesja: idzie się w górę od claude.exe (np. przez pwsh.exe) aż
/// do terminala. Wynik zapamiętuje się na całe życie procesu sesji. Port Resolve-HostPid.
/// </summary>
public sealed class HostResolver
{
    private readonly Dictionary<int, int> _cache = [];

    public int Resolve(int claudePid)
    {
        if (_cache.TryGetValue(claudePid, out var cached)) return cached;

        var found = 0;
        var id = claudePid;
        for (var depth = 0; depth < 6 && id != 0; depth++)
        {
            using var searcher = new ManagementObjectSearcher($"SELECT Name, ParentProcessId FROM Win32_Process WHERE ProcessId={id}");
            using var results = searcher.Get();
            ManagementObject? process = null;
            foreach (ManagementObject item in results) { process = item; break; }
            if (process is null) break;
            var name = (string?)process["Name"] ?? "";
            if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;

            if (HasWindow(id)) { found = id; break; }
            id = Convert.ToInt32(process["ParentProcessId"]);
        }
        _cache[claudePid] = found;
        return found;
    }

    /// <summary>Sesja zamknęła się — zapomnij, gdzie mieszkało jej okno.</summary>
    public void Forget(int claudePid) => _cache.Remove(claudePid);

    private static bool HasWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
