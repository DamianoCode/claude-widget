using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ClaudeWidget.Core;

/// <summary>Odczyt i zapis plików stanu, bezpieczny przy równoczesnych piszących i czytających.</summary>
public static class JsonStore
{
    private const int RenameAttempts = 5;
    private const int RenameBackoffMs = 15;

    /// <summary>
    /// Plik albo null, gdy go nie ma, jest zajęty albo nie jest poprawnym JSON-em. Czyta z pełnym
    /// udostępnieniem, bo hook i statusline podmieniają pliki w trakcie.
    /// </summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Zapis do pliku tymczasowego i podmiana nazwą: czytający nigdy nie zobaczy połowy pliku.
    /// Podmiana może chwilowo nie przejść na Windows, gdy ktoś akurat trzyma plik otwarty.
    /// </summary>
    public static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, type));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= RenameAttempts)
                {
                    Remove(temporary);
                    throw;
                }
                Thread.Sleep(RenameBackoffMs);
            }
        }
    }

    /// <summary>Usuwa plik, jeśli jest; brak pliku albo chwilowa blokada nie są błędem.</summary>
    public static void Remove(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // plik mógł już zniknąć albo być chwilowo otwarty; następna próba go zbierze
        }
    }
}
