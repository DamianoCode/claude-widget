using System.Text.RegularExpressions;

namespace ClaudeWidget;

/// <summary>
/// Jedna instancja na katalog stanu: drugi start po prostu się kończy. Ta sama nazwa muteksu co
/// w widget.ps1, żeby stary i nowy widżet nie działały równocześnie na tym samym katalogu.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? Acquire(string stateDir)
    {
        var key = "Local\\ClaudeCodeWidget-" + NonAsciiLetterOrDigit().Replace(stateDir.ToLowerInvariant(), "_");
        var mutex = new Mutex(false, key);
        if (!mutex.WaitOne(0))
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAsciiLetterOrDigit();
}
