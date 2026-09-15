using ClaudeWidget.Core.Hook;

namespace ClaudeWidget.Tests.Hook;

// Port of the "headless sessions (claude -p, SDK) never reach the widget" rule from
// tests/hook.test.mjs, exercised directly against the isAttendedSession port.
public sealed class AttendedSessionTests
{
    [Fact]
    public void Attended_by_default_when_no_environment_variables_are_set()
    {
        Assert.True(AttendedSession.IsAttended(Env()));
    }

    [Fact]
    public void Not_attended_when_entrypoint_is_not_cli()
    {
        Assert.False(AttendedSession.IsAttended(Env(entrypoint: "sdk-cli")));
    }

    [Fact]
    public void Attended_when_entrypoint_is_cli()
    {
        Assert.True(AttendedSession.IsAttended(Env(entrypoint: "cli")));
    }

    [Fact]
    public void Not_attended_when_session_attended_flag_is_zero()
    {
        Assert.False(AttendedSession.IsAttended(Env(sessionAttended: "0")));
    }

    private static Func<string, string?> Env(string? entrypoint = null, string? sessionAttended = null) => name => name switch
    {
        "CLAUDE_CODE_ENTRYPOINT" => entrypoint,
        "CLAUDE_CODE_SESSION_ATTENDED" => sessionAttended,
        _ => null,
    };
}
