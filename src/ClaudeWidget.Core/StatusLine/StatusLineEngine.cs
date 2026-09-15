using System.Text.Json;

namespace ClaudeWidget.Core.StatusLine;

/// <summary>
/// Statusline Claude Code, która przy okazji zasila widżet: zapisuje kontekst sesji i limity
/// konta (5 h i tygodniowy), a do terminala wypisuje jedną krótką linię.
///
/// Linia statusu musi się pokazać zawsze — nawet gdy zapis stanu widżetu się nie uda.
/// </summary>
public static class StatusLineEngine
{
    /// <summary>Zapisuje kontekst sesji (usage) i — jeśli obecne — limity konta.</summary>
    public static void Record(WidgetPaths paths, JsonElement data, long nowMs)
    {
        var sessionId = GetString(data, "session_id");
        var path = paths.SessionFile(sessionId, SessionFileKind.Usage);
        if (path is not null)
        {
            var context = GetProperty(data, "context_window");
            var usage = new SessionUsage
            {
                Name = GetString(data, "session_name") ?? string.Empty,
                Project = Path.GetFileName(ProjectDir(data)),
                Model = ModelDisplayName(data) ?? string.Empty,
                ContextPct = NumberOrNull(context, "used_percentage"),
                ContextTokens = NumberOrNull(context, "total_input_tokens"),
                ContextSize = NumberOrNull(context, "context_window_size"),
                UpdatedAt = nowMs,
            };
            JsonStore.Write(path, usage, StateJson.Default.SessionUsage);
        }

        // Limity są wspólne dla całego konta, więc wystarczy ostatni odczyt z dowolnej sesji.
        // rate_limits przychodzi tylko w planach Pro i Max, i to po pierwszej odpowiedzi w sesji.
        var limits = GetProperty(data, "rate_limits");
        var fiveHour = GetProperty(limits, "five_hour");
        var sevenDay = GetProperty(limits, "seven_day");
        if (IsObject(fiveHour) || IsObject(sevenDay))
        {
            JsonStore.Write(paths.LimitsFile, new AccountLimits
            {
                FiveHour = LimitWindowFrom(fiveHour),
                SevenDay = LimitWindowFrom(sevenDay),
                UpdatedAt = nowMs,
            }, StateJson.Default.AccountLimits);
        }
    }

    /// <summary>Buduje linię statusu wypisywaną do terminala Claude Code.</summary>
    public static string Render(JsonElement data)
    {
        var parts = new List<string>();
        var model = ModelDisplayName(data);
        if (!string.IsNullOrEmpty(model)) parts.Add(model);
        PushPercent(parts, "kontekst", NumberOrNull(GetProperty(data, "context_window"), "used_percentage"));
        var limits = GetProperty(data, "rate_limits");
        PushPercent(parts, "5 h", NumberOrNull(GetProperty(limits, "five_hour"), "used_percentage"));
        PushPercent(parts, "tydzień", NumberOrNull(GetProperty(limits, "seven_day"), "used_percentage"));
        return string.Join(" · ", parts);
    }

    private static string ProjectDir(JsonElement data) =>
        GetString(GetProperty(data, "workspace"), "project_dir") ?? GetString(data, "cwd") ?? string.Empty;

    private static string? ModelDisplayName(JsonElement data) => GetString(GetProperty(data, "model"), "display_name");

    private static LimitWindow? LimitWindowFrom(JsonElement? window) =>
        IsObject(window) ? new LimitWindow { Pct = NumberOrNull(window, "used_percentage"), ResetsAt = NumberOrNull(window, "resets_at") } : null;

    private static void PushPercent(List<string> parts, string label, double? value)
    {
        if (value.HasValue && double.IsFinite(value.Value))
        {
            parts.Add($"{label} {(long)Math.Round(value.Value, MidpointRounding.AwayFromZero)}%");
        }
    }

    private static bool IsObject(JsonElement? element) => element is { ValueKind: JsonValueKind.Object };

    private static string? GetString(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        if (!value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static JsonElement? GetProperty(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        return value.TryGetProperty(name, out var property) ? property : null;
    }

    private static double? NumberOrNull(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value) return null;
        if (!value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.Number ? property.GetDouble() : null;
    }
}
