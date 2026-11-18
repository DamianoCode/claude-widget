using ClaudeWidget.Core.Hook;

namespace ClaudeWidget.Tests.Hook;

// Port of the "headless sessions (claude -p, SDK) never reach the widget" rule from
// tests/hook.test.mjs, exercised directly against the isAttendedSession port.
public sealed class AttendedSessionTests
{
    [Fact]
    public void Attended_by_default_when_no_environment_variables_are_set()
    {
        Assert.True(AttendedSession.IsAttended(Env(), NeverAsked));
    }

    [Fact]
    public void Not_attended_when_entrypoint_is_not_cli()
    {
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "sdk-cli"), NeverAsked));
    }

    [Fact]
    public void Attended_when_entrypoint_is_cli()
    {
        Assert.True(AttendedSession.IsAttended(Env(entrypoint: "cli"), NeverAsked));
    }

    [Fact]
    public void Not_attended_when_session_attended_flag_is_zero()
    {
        Assert.False(AttendedSession.IsAttended(Env(sessionAttended: "0"), NeverAsked));
    }

    // Środowisko hooka w czacie Orca 1.4.263: Claude Code daje mu CLAUDE_CODE_SESSION_ATTENDED=0.
    [Fact]
    public void Orca_chat_with_an_agent_id_is_attended_although_claude_code_marks_it_unattended()
    {
        Assert.True(AttendedSession.IsAttended(Env(entrypoint: "orca", sessionAttended: "0", orcaAgent: "dfbd0007-1109-4766-9512-58f3bb8b4dfc"), LaunchedByOrca));
    }

    [Fact]
    public void Orca_entrypoint_without_an_agent_id_is_not_attended()
    {
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "orca", sessionAttended: "0"), LaunchedByOrca));
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "orca", sessionAttended: "1"), LaunchedByOrca));
    }

    // claude -p albo skrypt SDK odpalony z czatu Orca: zmienne czatu odziedziczone, ale rodzicem
    // claude nie jest Orca.
    [Fact]
    public void A_headless_run_started_from_an_orca_chat_is_not_attended()
    {
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "orca", sessionAttended: "0", orcaAgent: "a1"), () => false));
    }

    [Fact]
    public void A_cli_session_inside_orca_still_honours_the_unattended_flag()
    {
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "cli", sessionAttended: "0", orcaAgent: "a1"), NeverAsked));
    }

    [Fact]
    public void An_orca_agent_id_does_not_let_other_headless_entrypoints_through()
    {
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "sdk-cli", orcaAgent: "a1"), NeverAsked));
    }

    private static bool LaunchedByOrca() => true;

    // Sesja spoza czatu Orca nie może kosztować hooka sprawdzania procesu rodzica.
    private static bool NeverAsked() => throw new InvalidOperationException("launchedByOrca asked outside an Orca chat");

    private static Func<string, string?> Env(string? entrypoint = null, string? sessionAttended = null, string? orcaAgent = null) => name => name switch
    {
        "CLAUDE_CODE_ENTRYPOINT" => entrypoint,
        "CLAUDE_CODE_SESSION_ATTENDED" => sessionAttended,
        "ORCA_AGENT_SESSION_ID" => orcaAgent,
        _ => null,
    };
}
