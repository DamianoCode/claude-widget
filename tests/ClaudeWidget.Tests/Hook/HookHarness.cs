using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Hook;

namespace ClaudeWidget.Tests.Hook;

/// <summary>
/// In-process odpowiednik funkcji `hookEvents` z tests/hook.test.mjs: woła HookEngine tak, jak
/// robi to ClaudeWidgetHook.exe hook, i czyta z powrotem plik stanu sesji.
/// </summary>
internal sealed class HookHarness(string stateDir, string sessionId)
{
    private readonly WidgetPaths _paths = new(stateDir);
    // Zaczyna od prawdziwego czasu, żeby testy porównujące updatedAt z zegarem ściennym miały sens.
    private long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public int Pid { get; set; } = 4242;

    /// <summary>Parsuje surowy JSON na dodatkowe pola zdarzenia — odpowiednik obiektowego literału w JS.</summary>
    public static JsonObject Extra(string json) => (JsonObject)JsonNode.Parse(json)!;

    public SessionState? Send(string eventName, JsonObject? extra = null)
    {
        var input = new JsonObject
        {
            ["hook_event_name"] = eventName,
            ["session_id"] = sessionId,
            ["cwd"] = "C:\\apps\\api",
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra) input[key] = value?.DeepClone();
        }

        _now += 1;
        using var document = JsonDocument.Parse(input.ToJsonString());
        HookEngine.Handle(_paths, document.RootElement, Pid, _now);

        var path = _paths.SessionFile(sessionId, SessionFileKind.State);
        return path is not null && File.Exists(path) ? JsonStore.Read(path, StateJson.Default.SessionState) : null;
    }
}
