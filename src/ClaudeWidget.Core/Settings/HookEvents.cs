namespace ClaudeWidget.Core.Settings;

/// <summary>Jedno zdarzenie, na które reaguje hook, i czy jego wpis w settings.json ma być async.</summary>
public sealed record HookEventSpec(string Name, bool Async);

/// <summary>
/// Zdarzenia, na które reaguje hook. Te po wykonaniu narzędzia idą w tle (async): zdarzają się
/// przy każdym narzędziu, a stan zmieniają tylko z „czeka” na „pracuje”.
/// </summary>
public static class HookEvents
{
    public static readonly IReadOnlyList<HookEventSpec> All =
    [
        new HookEventSpec("SessionStart", false),
        new HookEventSpec("UserPromptSubmit", false),
        new HookEventSpec("PermissionRequest", false),
        new HookEventSpec("PermissionDenied", true),
        new HookEventSpec("PostToolUse", true),
        new HookEventSpec("PostToolUseFailure", true),
        new HookEventSpec("PostToolBatch", true),
        new HookEventSpec("Notification", false),
        new HookEventSpec("Stop", false),
        new HookEventSpec("StopFailure", false),
        new HookEventSpec("SessionEnd", false),
    ];
}
