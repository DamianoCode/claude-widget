using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeWidget.Core.Agents;

/// <summary>
/// Łączy bieżącą listę `claude agents --json --all` z poprzednią, żeby wiedzieć, od kiedy sesja
/// jest w danym stanie i czy widżet widział, jak kończyła pracę. Port <c>track()</c> z agents.mjs.
/// </summary>
public static partial class AgentsTracker
{
    // Skończona sesja w tle zostaje na liście jeszcze przez 12 h, potem znika z widżetu
    // (w widoku agentów zostaje, dopóki jej nie usuniesz).
    private const long RecentMs = 12 * 3_600_000L;
    private const int WaitingMax = 70;

    private static readonly (Regex Pattern, string Label)[] WaitingLabels =
    [
        (InputQuestionPattern(), "Czeka na Twoją odpowiedź"),
        (PermissionPattern(), "Czeka na Twoją zgodę"),
        (LoginPattern(), "Wymaga ponownego logowania"),
    ];

    public static IReadOnlyList<AgentSession> Track(IReadOnlyList<AgentEntry> listed, IReadOnlyList<AgentSession> previous, long now)
    {
        var before = new Dictionary<string, AgentSession>();
        foreach (var session in previous) before[session.SessionId] = session;

        var sessions = new List<AgentSession>();
        foreach (var entry in listed)
        {
            if (string.IsNullOrEmpty(entry.SessionId)) continue;
            var background = entry.Kind == "background";
            var state = background ? entry.State ?? "working" : null;
            if (state == "stopped") continue; // zatrzymałeś ją sam

            before.TryGetValue(entry.SessionId, out var prior);
            var changed = prior is null || prior.State != state;
            long since = !changed
                ? prior!.Since
                : prior is not null
                    ? now
                    : state == "working" && entry.StartedAt is double startedAt ? (long)startedAt : now;
            // Wynik jest nowy tylko wtedy, gdy widżet widział, jak sesja kończyła pracę. Sesja zastana
            // już skończona — np. po restarcie komputera — nie zapala zielonego.
            var fresh = state == "done" && (changed ? prior is not null : prior!.Fresh);
            if (background && state is "done" or "failed" && now - since > RecentMs) continue;

            sessions.Add(new AgentSession
            {
                SessionId = entry.SessionId,
                Id = !string.IsNullOrEmpty(entry.Id) ? entry.Id : entry.SessionId[..Math.Min(8, entry.SessionId.Length)],
                Kind = background ? "background" : "interactive",
                Pid = entry.Pid,
                Cwd = entry.Cwd ?? "",
                Name = entry.Name ?? "",
                Status = entry.Status ?? "",
                StartedAt = entry.StartedAt is double at ? (long)at : null,
                State = state,
                WaitingFor = DescribeWaiting(entry.WaitingFor),
                Since = since,
                Fresh = fresh,
            });
        }
        return sessions;
    }

    /// <summary>
    /// `waitingFor` nazywa to, na co sesja czeka. Znane hasła tłumaczy się na polski, resztę
    /// pokazuje bez zmian. Na wypadek zmiany formatu przyjmuje się też obiekt z jednym z pól
    /// question/message/description/tool/type.
    /// </summary>
    public static string DescribeWaiting(JsonElement? waitingFor)
    {
        string? text = null;
        if (waitingFor is { ValueKind: JsonValueKind.String } stringValue) text = stringValue.GetString();
        else if (waitingFor is { ValueKind: JsonValueKind.Object } objectValue)
        {
            foreach (var field in new[] { "question", "message", "description", "tool", "type" })
            {
                if (objectValue.TryGetProperty(field, out var candidate) && candidate.ValueKind == JsonValueKind.String)
                {
                    text = candidate.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(text)) return "";
        var trimmed = text.Trim();
        foreach (var (pattern, label) in WaitingLabels)
        {
            if (pattern.IsMatch(trimmed)) return label;
        }
        return Shorten(trimmed);
    }

    private static string Shorten(string text) => text.Length > WaitingMax ? $"{text[..(WaitingMax - 1)]}…" : text;

    [GeneratedRegex("input|question|answer", RegexOptions.IgnoreCase)]
    private static partial Regex InputQuestionPattern();

    [GeneratedRegex("permission|approv", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionPattern();

    [GeneratedRegex("login|auth", RegexOptions.IgnoreCase)]
    private static partial Regex LoginPattern();
}
