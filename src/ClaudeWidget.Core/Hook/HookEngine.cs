using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeWidget.Core.Hook;

/// <summary>
/// Hook widżetu Claude Code: zamienia zdarzenia sesji na stan sygnalizatora.
///   czeka   — Claude prosi o zgodę, zadaje pytanie albo tura skończyła się błędem API
///   pracuje — Claude wykonuje zadanie, także gdy tura skończyła się, a praca trwa w tle
///   gotowe  — odpowiedź skończona; <see cref="SessionState.Fresh"/> mówi, czy to nowy wynik do
///             przejrzenia, czy sesja po prostu stoi (np. zaraz po starcie albo po przerwaniu tury)
///
/// Zapisuje też PID procesu Claude Code, żeby widżet mógł rozpoznać sesję zamkniętą bez
/// SessionEnd i przełączyć się do okna jej terminala.
/// </summary>
public static partial class HookEngine
{
    private const int DetailMax = 40;
    private const int QuestionMax = 70;
    private const int SummaryMax = 160;

    // Monitor potrafi obserwować coś bez końca (np. `tail -f`), więc nie liczy się jako praca,
    // na którą czekasz — inaczej żółte świeciłoby w nieskończoność.
    private static readonly HashSet<string> NotWork = ["monitor"];

    /// <summary>
    /// Cała obsługa jednego zdarzenia hooka: czyta poprzedni stan, liczy przejście i zapisuje wynik.
    /// Niczego nie wypisuje i nigdy nie rzuca — obserwacja nigdy nie może zepsuć sesji.
    /// </summary>
    public static void Handle(WidgetPaths paths, JsonElement input, int pid, long nowMs)
    {
        var sessionId = GetString(input, "session_id");
        var path = paths.SessionFile(sessionId, SessionFileKind.State);
        if (path is null) return;

        var eventName = GetString(input, "hook_event_name");
        if (eventName == "SessionEnd")
        {
            JsonStore.Remove(paths.SessionFile(sessionId, SessionFileKind.State));
            JsonStore.Remove(paths.SessionFile(sessionId, SessionFileKind.Usage));
            JsonStore.Remove(paths.SessionFile(sessionId, SessionFileKind.Seen));
            return;
        }

        var previous = JsonStore.Read(path, StateJson.Default.SessionState);
        var next = Transition(eventName, input, previous, nowMs);

        if (next is null)
        {
            // Bez zmiany stanu dopisuje się tylko PID — gdy sesję przejął nowy proces albo pochodzi
            // sprzed wersji hooka z PID. Czas zapisu też: proces, który ruszył po nim, widżet uznaje
            // za obcy.
            if (previous is not null && previous.Pid != pid)
            {
                JsonStore.Write(path, previous with { Pid = pid, UpdatedAt = nowMs }, StateJson.Default.SessionState);
            }
            return;
        }

        var cwd = GetString(input, "cwd") ?? previous?.Cwd ?? string.Empty;
        var transcript = GetString(input, "transcript_path") ?? previous?.TranscriptPath;
        JsonStore.Write(path, next with { Cwd = cwd, TranscriptPath = transcript, Pid = pid, UpdatedAt = nowMs }, StateJson.Default.SessionState);
    }

    /// <summary>Liczy nowy stan sesji dla jednego zdarzenia, albo null, gdy zdarzenie niczego nie zmienia.</summary>
    public static SessionState? Transition(string? eventName, JsonElement input, SessionState? previous, long now)
    {
        switch (eventName)
        {
            case "SessionStart":
                return new SessionState { State = SessionStates.Done, Since = now, Fresh = false };

            case "UserPromptSubmit":
                return new SessionState { State = SessionStates.Working, Since = now, TurnStartedAt = now };

            case "PermissionRequest":
            {
                var toolName = GetString(input, "tool_name");
                var toolInput = GetProperty(input, "tool_input");
                return new SessionState
                {
                    State = SessionStates.Waiting,
                    Since = now,
                    TurnStartedAt = previous?.TurnStartedAt ?? now,
                    Detail = DescribeRequest(toolName, toolInput),
                    PendingTool = BuildPendingTool(toolName, toolInput),
                };
            }

            case "PostToolUse":
            case "PostToolUseFailure":
            {
                // Narzędzie, o które była prośba, wykonało się (także z błędem), więc zgodę udzielono.
                // Inne narzędzia z tej samej porcji mogą skończyć się w trakcie oczekiwania na zgodę,
                // więc nie wolno im zgasić czerwonego.
                var toolName = GetString(input, "tool_name");
                var toolInput = GetProperty(input, "tool_input");
                return AwaitingPermission(previous) && IsPendingTool(previous!.PendingTool!.Value, toolName, toolInput)
                    ? Resume(previous, now)
                    : null;
            }

            case "PermissionDenied":
            case "PostToolBatch":
                // Porcja narzędzi rozstrzygnięta: każda prośba o zgodę dostała odpowiedź — także
                // odmowę, po której PostToolUse nigdy nie przychodzi. Claude pracuje dalej.
                return AwaitingPermission(previous) ? Resume(previous, now) : null;

            case "Stop":
            {
                // Tura się skończyła, ale agenci albo polecenia w tle jeszcze pracują i obudzą sesję.
                var background = InFlight(GetProperty(input, "background_tasks"));
                if (background > 0)
                {
                    var started = previous?.TurnStartedAt ?? now;
                    return new SessionState
                    {
                        State = SessionStates.Working,
                        Since = started,
                        TurnStartedAt = started,
                        Background = background,
                        Detail = $"w tle: {CountTasks(background)}",
                    };
                }
                return new SessionState { State = SessionStates.Done, Since = now, Fresh = true, Summary = Summarize(GetString(input, "last_assistant_message")) };
            }

            case "StopFailure":
                return new SessionState { State = SessionStates.Waiting, Since = now, Detail = $"Błąd API: {GetString(input, "error") ?? "nieznany"}" };

            case "Notification":
            {
                var notificationType = GetString(input, "notification_type");
                if (notificationType == "permission_prompt" && previous?.State != SessionStates.Waiting)
                {
                    return new SessionState
                    {
                        State = SessionStates.Waiting,
                        Since = now,
                        TurnStartedAt = previous?.TurnStartedAt ?? now,
                        Detail = "Potrzebna Twoja decyzja",
                        PendingTool = BuildPendingTool("*", null),
                    };
                }
                // Stop nie przychodzi, gdy turę przerwano klawiszem Esc. idle_prompt jest wtedy
                // pierwszym sygnałem, że Claude już nie pracuje. Przerwałeś ją sam, więc nie ma
                // nowego wyniku do przejrzenia. Praca w tle trwa mimo ciszy w terminalu.
                if (notificationType == "idle_prompt" && previous?.State == SessionStates.Working && previous.Background is null or 0)
                {
                    return new SessionState { State = SessionStates.Done, Since = now, Fresh = false };
                }
                return null;
            }

            default:
                return null;
        }
    }

    // Czerwone z prośby o zgodę (a nie z błędu API) — tylko takie gaśnie, gdy narzędzia ruszą dalej.
    private static bool AwaitingPermission(SessionState? previous) =>
        previous is { State: SessionStates.Waiting } && previous.PendingTool.HasValue;

    private static SessionState Resume(SessionState? previous, long now)
    {
        var started = previous?.TurnStartedAt ?? now;
        return new SessionState { State = SessionStates.Working, Since = started, TurnStartedAt = started };
    }

    // Claude Code dopisuje do argumentów narzędzia to, co zebrał w oknie zgody — np. odpowiedzi
    // w AskUserQuestion. Wystarczy więc, że zgadza się wszystko, o co proszono.
    private static bool IsPendingTool(JsonElement pending, string? name, JsonElement? toolInput)
    {
        if (pending.ValueKind == JsonValueKind.String)
        {
            // zapis sprzed tej wersji: "nazwa\0argumenty"
            var legacy = pending.GetString() ?? string.Empty;
            return legacy.Split('\0')[0] == name;
        }
        if (pending.ValueKind != JsonValueKind.Object) return false;
        var pendingName = GetString(pending, "name");
        if (pendingName == "*") return true;
        var pendingInput = GetProperty(pending, "input");
        return pendingName == name && Contains(toolInput, pendingInput);
    }

    private static bool Contains(JsonElement? actual, JsonElement? expected)
    {
        if (expected is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } expectedValue)
        {
            return PrimitiveEquals(actual, expected);
        }
        if (actual is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } actualValue)
        {
            return false;
        }
        if (expectedValue.ValueKind == JsonValueKind.Array)
        {
            if (actualValue.ValueKind != JsonValueKind.Array) return false;
            var expectedItems = expectedValue.EnumerateArray().ToArray();
            var actualItems = actualValue.EnumerateArray().ToArray();
            if (expectedItems.Length != actualItems.Length) return false;
            for (var i = 0; i < expectedItems.Length; i++)
            {
                if (!Contains(actualItems[i], expectedItems[i])) return false;
            }
            return true;
        }
        // Tablica w miejscu obiektu nie ma pól: jak w JS, pasuje tylko do pustego obiektu.
        if (actualValue.ValueKind != JsonValueKind.Object) return !expectedValue.EnumerateObject().Any();
        foreach (var property in expectedValue.EnumerateObject())
        {
            JsonElement? actualProperty = actualValue.TryGetProperty(property.Name, out var value) ? value : null;
            if (!Contains(actualProperty, property.Value)) return false;
        }
        return true;
    }

    private static bool PrimitiveEquals(JsonElement? actual, JsonElement? expected)
    {
        var expectedKind = expected?.ValueKind ?? JsonValueKind.Undefined;
        var actualKind = actual?.ValueKind ?? JsonValueKind.Undefined;
        if (expectedKind == JsonValueKind.Null) return actualKind == JsonValueKind.Null;
        if (actualKind != expectedKind) return false;
        return expectedKind switch
        {
            JsonValueKind.String => actual!.Value.GetString() == expected!.Value.GetString(),
            JsonValueKind.Number => actual!.Value.GetDouble().Equals(expected!.Value.GetDouble()),
            JsonValueKind.True or JsonValueKind.False => actual!.Value.GetBoolean() == expected!.Value.GetBoolean(),
            _ => false,
        };
    }

    private static string DescribeRequest(string? name, JsonElement? toolInput)
    {
        var hasArgs = toolInput is { ValueKind: JsonValueKind.Object };
        var args = hasArgs ? toolInput!.Value : default;
        if (name == "AskUserQuestion")
        {
            string? question = null;
            if (hasArgs && args.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
            {
                var first = questions.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("question", out var q))
                {
                    question = AsText(q);
                }
            }
            return question is { Length: > 0 } ? $"Pytanie: {Shorten(question, QuestionMax)}" : "Pytanie do Ciebie";
        }
        if (name == "ExitPlanMode") return "Plan do zatwierdzenia";
        return $"Zgoda: {DescribeTool(name, hasArgs ? args : default, hasArgs)}";
    }

    // Jak path.basename w Node: ukośnik na końcu nie daje pustej nazwy.
    internal static string BaseName(string? path) => Path.GetFileName((path ?? string.Empty).TrimEnd('\\', '/'));

    private static string DescribeTool(string? name, JsonElement args, bool hasArgs)
    {
        var path = hasArgs ? GetString(args, "file_path") ?? GetString(args, "notebook_path") : null;
        string subject;
        if (!string.IsNullOrEmpty(path))
        {
            subject = BaseName(path);
        }
        else
        {
            var raw = hasArgs ? AsTextOrNull(args, "command") ?? AsTextOrNull(args, "url") ?? AsTextOrNull(args, "pattern") ?? string.Empty : string.Empty;
            subject = raw.Split('\n')[0];
        }
        var label = name ?? "narzędzie";
        return subject.Length > 0 ? $"{label} · {Shorten(subject, DetailMax)}" : label;
    }

    // Pierwsze zdanie odpowiedzi, bez znaczników Markdown — do podglądu wyniku w panelu.
    private static string Summarize(string? text)
    {
        if (text is null) return string.Empty;
        var withoutCode = CodeBlockRegex().Replace(text, " ");
        foreach (var row in NewlineRegex().Split(withoutCode))
        {
            var cleaned = EmphasisRegex().Replace(LeadingMarkerRegex().Replace(row, string.Empty), string.Empty).Trim();
            if (cleaned.Length == 0) continue;
            var match = SentenceRegex().Match(cleaned);
            var sentence = match.Success ? match.Value : cleaned;
            return Shorten(sentence, SummaryMax);
        }
        return string.Empty;
    }

    private static string Shorten(string text, int max) => text.Length > max ? $"{text[..(max - 1)]}…" : text;

    private static int InFlight(JsonElement? tasks)
    {
        if (tasks is not { ValueKind: JsonValueKind.Array } value) return 0;
        var count = 0;
        foreach (var task in value.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object) continue;
            var type = GetString(task, "type");
            if (type is null || !NotWork.Contains(type)) count++;
        }
        return count;
    }

    private static string CountTasks(int count)
    {
        var tens = count % 100;
        var word = count == 1 ? "zadanie" : count % 10 >= 2 && count % 10 <= 4 && (tens < 12 || tens > 14) ? "zadania" : "zadań";
        return $"{count} {word}";
    }

    private static JsonElement BuildPendingTool(string? name, JsonElement? input)
    {
        var node = new JsonObject
        {
            ["name"] = name,
            ["input"] = input.HasValue ? JsonNode.Parse(input.Value.GetRawText()) : null,
        };
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var value) ? value : null;
    }

    private static string? AsTextOrNull(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var value) ? AsText(value) : null;
    }

    private static string? AsText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    [GeneratedRegex("```[\\s\\S]*?```")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex("\\r?\\n")]
    private static partial Regex NewlineRegex();

    [GeneratedRegex(@"^\s*(?:#+|>|[-*+]|\d+[.)])\s+")]
    private static partial Regex LeadingMarkerRegex();

    [GeneratedRegex("[*_`]")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"^.*?[.!?](?=\s|$)")]
    private static partial Regex SentenceRegex();
}
