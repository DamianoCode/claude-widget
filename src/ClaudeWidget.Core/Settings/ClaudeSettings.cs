using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeWidget.Core.Settings;

/// <summary>Czyja jest statusline w settings.json — od tego zależy, czy widżet dostaje limity i kontekst.</summary>
public enum StatusLineKind
{
    /// <summary>Brak statusline — widżet nie dostaje danych.</summary>
    Missing,

    /// <summary>Statusline widżetu.</summary>
    Widget,

    /// <summary>Cudza statusline z przekaźnikiem widżetu na początku — dane dochodzą, wygląd bez zmian.</summary>
    Relayed,

    /// <summary>Cudza statusline bez przekaźnika — widżet nie dostaje danych.</summary>
    Foreign,
}

/// <summary>Powłoka, w której Claude Code uruchomi statusline: Git Bash, a gdy go nie ma — PowerShell.</summary>
public enum StatusLineShell { Bash, PowerShell }

/// <summary>
/// Wpisy widżetu w ~/.claude/settings.json: hooki i statusline. Rusza wyłącznie wpisy widżetu —
/// także te z wersji z Node (.claude/widget/hook.mjs, .claude/widget/statusline.mjs) — a przed
/// zapisem zostawia kopię pliku obok.
/// </summary>
public static partial class ClaudeSettings
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
    /// <summary>
    /// Nazwa przekaźnika przed cudzą statusline — kopia ClaudeWidgetHook.exe pod inną nazwą. Starsze
    /// wersje widżetu usuwają każdą statusline z „ClaudeWidgetHook.exe”; z tą nazwą cudza komenda
    /// przetrwa powrót do starszej wersji.
    /// </summary>
    public const string RelayExeName = "ClaudeWidgetRelay.exe";

    /// <param name="statusLineExePath">
    /// Ścieżka exe w komendzie statusline (domyślnie <paramref name="hookExePath"/>) — najlepiej bez znaków,
    /// które powłoka traktuje specjalnie; instalator podaje krótką nazwę 8.3 katalogu.
    /// </param>
    /// <param name="shell">
    /// Powłoka, w której Claude Code uruchomi statusline. Od niej zależy zapis komendy (PowerShell ścieżkę
    /// w cudzysłowie uruchomi tylko z operatorem &amp;) i to, czy wolno wpiąć przekaźnik przed cudzą
    /// statusline — tylko w Git Bash, bo w PowerShellu 5.1 potok między programami gubi znaki spoza ASCII.
    /// null — nieznana: zapis zrozumiały dla Git Bash, bez przekaźnika.
    /// </param>
    public static bool Install(string settingsPath, string hookExePath, string? statusLineExePath = null, StatusLineShell? shell = null)
    {
        var current = Load(settingsPath, out var existed);
        var (next, foreignStatusLine) = WithWidget(current, hookExePath, statusLineExePath, shell);
        // Nic się nie zmienia (np. przy każdym starcie widżetu) — bez zapisu i bez kolejnej kopii zapasowej.
        if (existed && JsonNode.DeepEquals(current, next)) return foreignStatusLine;
        PrepareForWrite(settingsPath, existed);
        Save(settingsPath, next);
        return foreignStatusLine;
    }

    /// <summary>Ścieżka, którą Git Bash i PowerShell przyjmą bez cudzysłowu: litery, cyfry i . _ - / \ : ~</summary>
    public static bool IsShellSafe(string path) =>
        path.Length > 0 && path.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or '\\' or ':' or '~');

    /// <summary>Czyja jest statusline w pliku; nieczytelny plik liczy się jak cudza statusline.</summary>
    public static StatusLineKind Inspect(string settingsPath)
    {
        JsonObject settings;
        try
        {
            settings = Load(settingsPath, out _);
        }
        catch (InvalidOperationException)
        {
            return StatusLineKind.Foreign;
        }
        if (settings["statusLine"] is not JsonObject statusLine) return StatusLineKind.Missing;
        if (RelayPrefix().IsMatch(AsString(statusLine["command"]))) return StatusLineKind.Relayed;
        return IsWidgetStatusLine(statusLine) ? StatusLineKind.Widget : StatusLineKind.Foreign;
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
        if (next["statusLine"] is JsonObject statusLine)
        {
            var command = AsString(statusLine["command"]);
            var relay = RelayPrefix().Match(command);
            // Przekaźnik przed cudzą statusline: zdejmuje się tylko jego — cudza komenda wraca bez zmian.
            if (relay.Success) statusLine["command"] = command[relay.Length..];
            else if (IsWidgetStatusLine(statusLine)) next.Remove("statusLine");
        }
        return next;
    }

    /// <summary>
    /// Najpierw zdejmuje stare wpisy widżetu, więc ponowna instalacja niczego nie dubluje.
    /// Cudzej statusline nie nadpisuje — bez niej widżet nie zna tylko limitów i kontekstu.
    /// </summary>
    public static (JsonObject Settings, bool ForeignStatusLine) WithWidget(
        JsonObject settings, string hookExePath, string? statusLineExePath = null, StatusLineShell? shell = null)
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
        var exePath = statusLineExePath ?? hookExePath;
        var relayPath = Normalize(Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, RelayExeName));
        var foreignStatusLine = false;
        if (next["statusLine"] is null)
        {
            next["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = $"{CommandPath(exePath, shell)} statusline" };
        }
        else if (shell == StatusLineShell.Bash && IsShellSafe(relayPath)
            && next["statusLine"] is JsonObject foreign && CanRelay(AsString(foreign["command"])))
        {
            // Przekaźnik zapisuje dane dla widżetu i oddaje wejście cudzej komendzie bez zmian. Gdy go
            // zabraknie (nieudane odinstalowanie, aktualizacja właśnie podmienia pliki), `|| cat` oddaje
            // wejście dalej — cudza statusline działa i bez widżetu.
            foreign["command"] = $"{{ {relayPath} tee || cat; }} 2>/dev/null | {AsString(foreign["command"])}";
        }
        else
        {
            foreignStatusLine = true;
        }
        return (next, foreignStatusLine);
    }

    // Ścieżka bez znaków specjalnych idzie bez cudzysłowu — tak rozumieją ją i Git Bash, i PowerShell.
    // Inaczej zapis zależy od powłoki: PowerShell uruchomi ścieżkę w cudzysłowie tylko z operatorem &.
    private static string CommandPath(string path, StatusLineShell? shell)
    {
        var normalized = Normalize(path);
        if (IsShellSafe(normalized)) return normalized;
        return shell == StatusLineShell.PowerShell
            ? $"& '{normalized.Replace("'", "''")}'"
            : $"\"{normalized.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`")}\"";
    }

    // Przekaźnik wpina się tylko przed prostą komendą: w złożonej (a && b, a; b, a | b) wejście
    // dostałaby tylko pierwsza część i cudza linia statusu mogłaby się rozsypać.
    private static bool CanRelay(string command) =>
        command.Trim().Length > 0
        && command.IndexOfAny(['&', '|', ';', '\n', '\r', '`']) < 0
        && !command.Contains("$(", StringComparison.Ordinal);

    [GeneratedRegex(@"^\s*\{\s*[^{}]*?ClaudeWidgetRelay\.exe\s+tee\s*\|\|\s*cat;\s*\}\s*2>/dev/null\s*\|\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RelayPrefix();

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
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
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
            var parsed = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            // JsonObject rozwija się leniwie: duplikat klucza wyszedłby dopiero po zrobieniu kopii
            // zapasowej, więc rozwija się go tutaj.
            _ = parsed.Count;
            return parsed;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"Nie da się odczytać {path}: {error.Message}. Niczego nie zmieniono.", error);
        }
    }

    // Jak JSON.stringify(value, null, 2) + "\n" w wersji z Node: plik edytujesz ręcznie, więc polskie
    // znaki, cudzysłowy i && zostają czytelne zamiast \uXXXX, a wiersze kończą się LF.
    private static void Save(string path, JsonObject value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            value.WriteTo(writer);
        }
        buffer.WriteByte((byte)'\n');
        File.WriteAllBytes(path, buffer.ToArray());
    }
}
