using System.Diagnostics;
using System.Management;

namespace ClaudeWidget.Native;

/// <summary>
/// Proces z oknem, w którym działa sesja: idzie się w górę od claude.exe (np. przez pwsh.exe) aż
/// do terminala. Wynik zapamiętuje się na całe życie procesu sesji. Port Resolve-HostPid.
///
/// Zapytania WMI bywają wolne (setki ms, czasem sekundy przy pierwszym użyciu), więc liczy się je
/// zawsze w tle (Task.Run) — nigdy na wątku UI (timer co 1 s, klik wiersza, skrót klawiszowy).
/// </summary>
public sealed class HostResolver
{
    private readonly Dictionary<int, Task<int>> _cache = [];

    /// <summary>Wynik z pamięci podręcznej, jeśli już policzony; w przeciwnym razie zleca policzenie w tle i zwraca false.</summary>
    public bool TryGetCached(int claudePid, out int hostPid)
    {
        if (_cache.TryGetValue(claudePid, out var task) && task.IsCompletedSuccessfully)
        {
            hostPid = task.Result;
            return true;
        }
        StartResolving(claudePid);
        hostPid = 0;
        return false;
    }

    public Task<int> ResolveAsync(int claudePid) => StartResolving(claudePid);

    private Task<int> StartResolving(int claudePid)
    {
        // Nieudane zapytanie WMI (np. usługa jeszcze nie ruszyła) liczy się od nowa przy następnej
        // okazji — inaczej kliknięcie tej sesji do końca jej życia nie przenosiłoby do terminala.
        if (_cache.TryGetValue(claudePid, out var existing) && !existing.IsFaulted) return existing;
        var task = Task.Run(() => ResolveCore(claudePid));
        _cache[claudePid] = task;
        return task;
    }

    /// <summary>Sesja zamknęła się — zapomnij, gdzie mieszkało jej okno.</summary>
    public void Forget(int claudePid) => _cache.Remove(claudePid);

    private static int ResolveCore(int claudePid)
    {
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
        return found;
    }

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
