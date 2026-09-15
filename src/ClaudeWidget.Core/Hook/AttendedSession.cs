namespace ClaudeWidget.Core.Hook;

/// <summary>
/// Sesje uruchamiane ze skryptów (<c>claude -p</c>, SDK) i bez obsługi nie trafiają do widżetu —
/// ta sama reguła co dawniej w hooks/notify.ps1 i store.mjs.
/// </summary>
public static class AttendedSession
{
    public static bool IsAttended(Func<string, string?> getEnvironmentVariable)
    {
        var entry = getEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT");
        if (!string.IsNullOrEmpty(entry) && entry != "cli") return false;
        return getEnvironmentVariable("CLAUDE_CODE_SESSION_ATTENDED") != "0";
    }

    public static bool IsAttended() => IsAttended(Environment.GetEnvironmentVariable);
}
