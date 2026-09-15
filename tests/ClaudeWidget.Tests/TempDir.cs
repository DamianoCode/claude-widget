namespace ClaudeWidget.Tests;

/// <summary>Katalog tymczasowy na czas jednego testu.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "claude-widget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // pozostałość w %TEMP% niczego nie psuje
        }
    }
}
