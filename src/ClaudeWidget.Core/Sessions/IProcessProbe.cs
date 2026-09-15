namespace ClaudeWidget.Core.Sessions;

/// <summary>Migawka procesu potrzebna do rozpoznania, czy PID z pliku stanu to wciąż ta sama sesja.</summary>
public sealed record ProcessSnapshot(string Name, long? StartTimeMs);

/// <summary>
/// Dostęp do listy procesów — osobny interfejs, żeby dało się podstawić fałszywe procesy w testach
/// (PID żywy/martwy/ponownie użyty) bez uruchamiania prawdziwych procesów.
/// </summary>
public interface IProcessProbe
{
    /// <summary>Migawka procesu o danym PID, albo null, gdy proces nie istnieje.</summary>
    ProcessSnapshot? Get(int pid);
}

/// <summary>Prawdziwe procesy systemowe przez System.Diagnostics.Process.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public ProcessSnapshot? Get(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            long? startMs;
            try
            {
                startMs = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Dostęp do StartTime bywa odmówiony (np. proces innego użytkownika); traktuje się
                // to jak brak informacji, nie jak zamkniętą sesję.
                startMs = null;
            }
            return new ProcessSnapshot(process.ProcessName, startMs);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
