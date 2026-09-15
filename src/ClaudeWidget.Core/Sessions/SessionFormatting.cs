using ClaudeWidget.Core.Text;

namespace ClaudeWidget.Core.Sessions;

/// <summary>Teksty karty i panelu dla jednej sesji: powód czekania, opis stanu, wiersz w panelu.</summary>
public static class SessionFormatting
{
    public static string GetWaitReason(SessionInfo session) =>
        !string.IsNullOrEmpty(session.Detail) ? session.Detail : "Potrzebna Twoja decyzja";

    public static string GetWaitSpan(SessionInfo session, long nowMs) => Formatting.GetWaitSpan(session.Since, nowMs);

    /// <summary>
    /// Przy sesji, która czeka, czas idzie na początek — na wąskiej karcie długi powód zostaje ucięty.
    /// </summary>
    public static string GetDetail(SessionInfo session, long nowMs)
    {
        var elapsed = nowMs - session.Since;
        return session.Kind switch
        {
            SessionKinds.Waiting => $"{GetWaitSpan(session, nowMs)} · {GetWaitReason(session)}",
            SessionKinds.Working => session.Background > 0
                ? $"{session.Detail} · {Formatting.FormatSpan(elapsed)}"
                : $"od {Formatting.FormatSpan(elapsed)}",
            SessionKinds.New => elapsed < 60000 ? "przed chwilą" : $"{Formatting.FormatSpan(elapsed)} temu",
            _ => elapsed < 60000 ? "przed chwilą" : $"od {Formatting.FormatSpan(elapsed)}",
        };
    }

    public static string GetSessionMeta(SessionInfo session, long nowMs)
    {
        var detail = GetDetail(session, nowMs);
        var what = session.Kind switch
        {
            SessionKinds.Waiting => $"czeka {GetWaitSpan(session, nowMs)} · {LowerFirst(GetWaitReason(session))}",
            SessionKinds.Working => $"pracuje {detail}",
            SessionKinds.New => $"nowy wynik · {detail}",
            _ => $"bezczynna {detail}",
        };
        var where = session.IsBackground ? $"{session.Project} · w tle" : session.Project;
        return $"{where} · {what}";
    }

    public static string GetContextNote(SessionInfo? session)
    {
        if (session is { ContextTokens: double tokens, ContextSize: double size })
        {
            return $"{Formatting.FormatTokens(tokens)} / {Formatting.FormatTokens(size)}";
        }
        return "";
    }

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
