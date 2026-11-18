using System.Runtime.InteropServices;

namespace ClaudeWidget.Hook;

/// <summary>
/// PID rodzica procesu bieżącego. Claude Code uruchamia hook bez powłoki (forma command + args),
/// więc rodzic to bezpośrednio proces Claude Code — dzięki temu widżet może rozpoznać sesję
/// zamkniętą bez SessionEnd.
/// </summary>
internal static partial class ParentProcess
{
    private const int ProcessBasicInformation = 0;

    public static int GetParentProcessId()
    {
        var info = default(ProcessBasicInformationData);
        var status = NtQueryInformationProcess(
            (nint)(-1), // pseudo-uchwyt bieżącego procesu
            ProcessBasicInformation,
            ref info,
            Marshal.SizeOf<ProcessBasicInformationData>(),
            out _);
        return status == 0 ? (int)info.InheritedFromUniqueProcessId : Environment.ProcessId;
    }

    /// <summary>
    /// Czy rodzicem procesu <paramref name="pid"/> jest Orca. Czat Orca to claude.exe uruchomiony
    /// wprost przez Orca.exe, a skrypt (<c>claude -p</c>) odpalony z tego czatu dziedziczy jego
    /// zmienne, ale rodzicem ma powłokę. Gdy czegoś nie da się sprawdzić — false.
    /// </summary>
    public static bool IsChildOfOrca(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0) return false;
        try
        {
            var info = default(ProcessBasicInformationData);
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, ref info, Marshal.SizeOf<ProcessBasicInformationData>(), out _) != 0)
            {
                return false;
            }
            using var parent = System.Diagnostics.Process.GetProcessById((int)info.InheritedFromUniqueProcessId);
            return parent.ProcessName.Equals("Orca", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        ref ProcessBasicInformationData processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }
}
