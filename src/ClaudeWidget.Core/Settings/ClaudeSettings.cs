using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeWidget.Core.Settings;

/// <summary>
/// Wpisy widżetu w ~/.claude/settings.json: hooki i statusline. Rusza wyłącznie wpisy widżetu —
/// także te z wersji z Node (.claude/widget/hook.mjs, .claude/widget/statusline.mjs) — a przed
/// zapisem zostawia kopię pliku obok.
/// </summary>
public static class ClaudeSettings
{
    private const string LegacyHookMark = ".claude/widget/hook.mjs";
    private const string LegacyStatusMark = ".claude/widget/statusline.mjs";
    private const string ExeMark = "ClaudeWidgetHook.exe";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>
    /// Dopisuje hooki i statusline wskazujące na <paramref name="hookExePath"/>, najpierw zdejmując
    /// wszystkie dawne wpisy widżetu. Cudzej statusline nie nadpisuje.
    /// </summary>
    /// <returns>true, gdy zostawiono cudzą statusline (widżet nie dostanie wtedy limitów ani kontekstu).</returns>
    public static bool Install(string settingsPath, string hookExePath)
    {
        var current = Load(settingsPath, out var existed);
        PrepareForWrite(settingsPath, existed);
        var (next, foreignStatusLine) = WithWidget(current, hookExePath);
        Save(settingsPath, next);
        return foreignStatusLine;
    }

    /// <summary>Usuwa wszystkie wpisy widżetu, aż do pustych grup i pustego obiektu hooks.</summary>
    public static void Remove(string settingsPath)
    {
        var current = Load(settingsPath, out var existed);
        PrepareForWrite(settingsPath, existed);
        Save(settingsPath, WithoutWidget(current));
    }

    /// <summary>Zdejmuje wpisy widżetu z <paramref name="settings"/>, nie ruszając reszty pliku.</summary>
    public static JsonObject WithoutWidget(JsonObject settings)
    {
        var next = settings.DeepClone()!.AsObject();
        if (next["hooks"] is JsonObject hooks)
        {
            foreach (var eventName in hooks.Select(entry => entry.Key).ToList())
            {
                if (hooks[eventName] is not JsonArray groups) continue;
                var kept = new JsonArray();
                foreach (var groupNode in groups)
                {
                    if (groupNode is JsonObject group && group["hooks"] is JsonArray groupHooks)
                    {
                        var filtered = new JsonArray(groupHooks.Where(hook => !IsWidgetHook(hook)).Select(hook => hook!.DeepClone()).ToArray());
                        if (filtered.Count == 0) continue;
                        var newGroup = group.DeepClone()!.AsObject();
                        newGroup["hooks"] = filtered;
                        AddNode(kept, newGroup);
                    }
                    else
                    {
                        AddNode(kept, groupNode?.DeepClone());
                    }
                }
                if (kept.Count > 0) hooks[eventName] = kept;
                else hooks.Remove(eventName);
            }
            if (hooks.Count == 0) next.Remove("hooks");
        }
        if (IsWidgetStatusLine(next["statusLine"])) next.Remove("statusLine");
        return next;
    }

    /// <summary>
    /// Najpierw zdejmuje stare wpisy widżetu, więc ponowna instalacja niczego nie dubluje.
    /// Cudzej statusline nie nadpisuje — bez niej widżet nie zna tylko limitów i kontekstu.
    /// </summary>
    public static (JsonObject Settings, bool ForeignStatusLine) WithWidget(JsonObject settings, string hookExePath)
    {
        var next = WithoutWidget(settings);
        var hooks = next["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            next["hooks"] = hooks;
        }
        foreach (var spec in HookEvents.All)
        {
            var groups = hooks[spec.Name] as JsonArray;
            if (groups is null)
            {
                groups = new JsonArray();
                hooks[spec.Name] = groups;
            }
            var hookEntry = new JsonObject
            {
                ["type"] = "command",
                ["command"] = hookExePath,
                ["args"] = new JsonArray("hook"),
                ["timeout"] = 5,
            };
            if (spec.Async) hookEntry["async"] = true;
            AddNode(groups, new JsonObject { ["hooks"] = new JsonArray(hookEntry) });
        }
        var foreignStatusLine = next["statusLine"] is not null;
        if (!foreignStatusLine)
        {
            next["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"\"{Normalize(hookExePath)}\" statusline",
            };
        }
        return (next, foreignStatusLine);
    }

    public static bool IsWidgetHook(JsonNode? hook)
    {
        if (hook is not JsonObject obj) return false;
        var command = AsString(obj["command"]);
        var parts = new List<string> { command };
        if (obj["args"] is JsonArray args)
        {
            foreach (var arg in args) parts.Add(AsString(arg));
        }
        var joined = Normalize(string.Join(' ', parts));
        return joined.Contains(LegacyHookMark, StringComparison.Ordinal) || joined.Contains(ExeMark, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWidgetStatusLine(JsonNode? statusLine)
    {
        if (statusLine is not JsonObject obj) return false;
        var command = Normalize(AsString(obj["command"]));
        return command.Contains(LegacyStatusMark, StringComparison.Ordinal) || command.Contains(ExeMark, StringComparison.OrdinalIgnoreCase);
    }

    // JsonArray.Add<T>(T) próbuje owinąć wartości nieprymitywne przez refleksję (niedozwolone przy
    // AOT/trymowaniu); węzeł JSON dokłada się więc przez interfejs listy, bez generycznego wrappera.
    private static void AddNode(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    private static string Normalize(string? text) => (text ?? string.Empty).Replace('\\', '/');

    private static string AsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static void PrepareForWrite(string settingsPath, bool existed)
    {
        if (existed)
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.Copy(settingsPath, $"{settingsPath}.bak-widget-{stamp}", overwrite: true);
        }
        else
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
    }

    private static JsonObject Load(string path, out bool existed)
    {
        if (!File.Exists(path))
        {
            existed = false;
            return new JsonObject();
        }
        existed = true;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Nie da się odczytać {path}: {error.Message}. Niczego nie zmieniono.", error);
        }
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"Nie da się odczytać {path}: {error.Message}. Niczego nie zmieniono.", error);
        }
    }

    private static void Save(string path, JsonObject value)
    {
        var json = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
