using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWidget;

/// <summary>widget-config.json: pozycja i rozmiar okna. Pisze i czyta wyłącznie widżet.</summary>
public sealed record WidgetConfig
{
    public double? Left { get; init; }

    public double? Top { get; init; }

    public string? Size { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetConfig))]
internal sealed partial class WidgetConfigJson : JsonSerializerContext;

public static class WidgetConfigStore
{
    public static WidgetConfig? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, WidgetConfigJson.Default.WidgetConfig);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Write(string path, WidgetConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, WidgetConfigJson.Default.WidgetConfig));
    }
}
