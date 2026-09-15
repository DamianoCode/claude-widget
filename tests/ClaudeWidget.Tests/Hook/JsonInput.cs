using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeWidget.Tests.Hook;

/// <summary>Buduje JsonElement z wygodnej notacji obiektowej — odpowiednik literałów JS w testach Node.</summary>
internal static class JsonInput
{
    public static JsonElement Object(params (string Name, JsonNode? Value)[] properties)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in properties) obj[name] = value;
        return Parse(obj.ToJsonString());
    }

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
