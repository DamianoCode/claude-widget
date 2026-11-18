using ClaudeWidget.Core.Ide;

namespace ClaudeWidget.Core.Hook;

/// <summary>
/// Sesje uruchamiane ze skryptów (<c>claude -p</c>, SDK) i bez obsługi nie trafiają do widżetu —
/// ta sama reguła co dawniej w hooks/notify.ps1 i store.mjs.
///
/// Wyjątek: czat Orca. Orca uruchamia w nim Claude Code z własnym punktem wejścia, a Claude Code daje
/// wtedy hookom <c>CLAUDE_CODE_SESSION_ATTENDED=0</c>, jak każdej sesji bez własnego terminala.
/// To jednak rozmowa z Tobą: ma identyfikator agenta od Orca, a claude.exe uruchomiła sama Orca.
/// Drugi warunek odsiewa skrypty (<c>claude -p</c>, SDK) odpalone z czatu — dziedziczą jego zmienne
/// razem z punktem wejścia, ale nie są jego dzieckiem.
/// </summary>
public static class AttendedSession
{
    /// <param name="launchedByOrca">Czy rodzicem procesu Claude Code jest Orca; pytane tylko dla punktu wejścia „orca”.</param>
    public static bool IsAttended(Func<string, string?> getEnvironmentVariable, Func<bool> launchedByOrca)
    {
        var entry = getEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT");
        if (entry == "orca") return !string.IsNullOrEmpty(getEnvironmentVariable(OrcaPlace.AgentSessionVariable)) && launchedByOrca();
        if (!string.IsNullOrEmpty(entry) && entry != "cli") return false;
        return getEnvironmentVariable("CLAUDE_CODE_SESSION_ATTENDED") != "0";
    }
}
