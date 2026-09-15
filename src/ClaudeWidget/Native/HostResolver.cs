using System.Diagnostics;
using System.Management;

namespace ClaudeWidget.Native;

/// <summary>
/// Proces z oknem, w którym działa sesja, i łańcuch procesów od claude.exe do niego (np. claude →
/// pwsh → terminale VS Code → VS Code). Po łańcuchu rozszerzenie VS Code rozpoznaje terminal sesji.
/// <see cref="Window"/> — dokładne okno terminala z konsoli sesji (<see cref="NativeMethods.ConsoleOwnerWindow"/>)
/// albo zero, gdy się go nie da ustalić.
/// </summary>
public sealed record HostInfo(int HostPid, IReadOnlyList<int> Chain, IntPtr Window = default)
{
    public static readonly HostInfo None = new(0, []);
}

/// <summary>
/// Idzie się w górę od claude.exe aż do procesu z oknem. Wynik zapamiętuje się na całe życie
/// procesu sesji. Port Resolve-HostPid.
///
/// Zapytania WMI bywają wolne (setki ms, czasem sekundy przy pierwszym użyciu), więc liczy się je
/// zawsze w tle (Task.Run) — nigdy na wątku UI (timer co 1 s, klik wiersza, skrót klawiszowy).
/// </summary>
public sealed class HostResolver
{
    private readonly Dictionary<int, Task<HostInfo>> _cache = [];

    /// <summary>Wynik z pamięci podręcznej, jeśli już policzony; w przeciwnym razie zleca policzenie w tle i zwraca false.</summary>
    public bool TryGetCached(int claudePid, out HostInfo host)
    {
        if (_cache.TryGetValue(claudePid, out var task) && task.IsCompletedSuccessfully)
        {
            host = task.Result;
            return true;
        }
        StartResolving(claudePid);
        host = HostInfo.None;
        return false;
    }

    public Task<HostInfo> ResolveAsync(int claudePid) => StartResolving(claudePid);

    private Task<HostInfo> StartResolving(int claudePid)
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

    private static HostInfo ResolveCore(int claudePid)
    {
        var window = NativeMethods.ConsoleOwnerWindow(claudePid);
        var chain = new List<int>();
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

            chain.Add(id);
            if (HasWindow(id)) return new HostInfo(id, chain, window);
            id = Convert.ToInt32(process["ParentProcessId"]);
        }
        return new HostInfo(0, chain, window);
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
