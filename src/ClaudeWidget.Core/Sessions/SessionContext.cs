namespace ClaudeWidget.Core.Sessions;

/// <summary>Zajętość kontekstu sesji: procent, tokeny, okno i czy procent jest szacunkiem.</summary>
public readonly record struct SessionContext(double? Pct, double? Tokens, double? Size, bool Estimated)
{
    public static SessionContext None => default;
}

public static class SessionContextExtensions
{
    public static SessionInfo WithContext(this SessionInfo session, SessionContext context) => session with
    {
        ContextPct = context.Pct,
        ContextTokens = context.Tokens,
        ContextSize = context.Size,
        ContextEstimated = context.Estimated,
    };
}
