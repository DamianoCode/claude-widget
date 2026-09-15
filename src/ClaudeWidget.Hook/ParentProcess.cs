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
