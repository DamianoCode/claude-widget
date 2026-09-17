using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget.Tests.Sessions;

public class AlertPlannerTests
{
    private static SessionInfo S(string id, string kind, bool background = false) =>
        new() { Id = id, Kind = kind, Project = "api", Name = id, IsBackground = background };

    [Fact]
    public void Sessions_found_at_start_are_not_announced()
    {
        var planner = new AlertPlanner();

        var changes = planner.Next([S("a", SessionKinds.Waiting), S("b", SessionKinds.New)], 0);

        Assert.Empty(changes.Raised);
        Assert.Empty(changes.Cleared);
    }

    [Fact]
    public void A_session_that_starts_waiting_is_announced_once_with_a_sound()
    {
        var planner = new AlertPlanner();
        planner.Next([S("a", SessionKinds.Working)], 0);

        var alert = Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], 1000).Raised);
        Assert.Equal(AlertKind.Waiting, alert.Kind);
        Assert.True(alert.Sound);
        Assert.Empty(planner.Next([S("a", SessionKinds.Waiting)], 2000).Raised);
    }

    [Fact]
    public void A_burst_of_permission_prompts_replaces_the_notification_but_plays_the_sound_once_per_15_seconds()
    {
        var planner = new AlertPlanner();
        planner.Next([S("a", SessionKinds.Working)], 0);
        planner.MarkSounded(Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], 1000).Raised), 1000);
        planner.Next([S("a", SessionKinds.Working)], 2000);

        Assert.False(Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], 3000).Raised).Sound);

        planner.Next([S("a", SessionKinds.Working)], 10_000);
        Assert.True(Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], 1000 + AlertPlanner.DefaultSoundThrottleMs).Raised).Sound);
    }

    [Theory]
    [InlineData(0, 3000, true)]
    [InlineData(60_000, 31_000, false)]
    public void The_repeat_limit_follows_the_setting(long throttleMs, long againAt, bool soundsAgain)
    {
        var planner = new AlertPlanner { SoundThrottleMs = throttleMs };
        planner.Next([S("a", SessionKinds.Working)], 0);
        planner.MarkSounded(Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], 1000).Raised), 1000);
        planner.Next([S("a", SessionKinds.Working)], 2000);

        Assert.Equal(soundsAgain, Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], againAt).Raised).Sound);
    }

    [Fact]
    public void An_alert_muted_because_you_were_looking_does_not_silence_the_next_one()
    {
        var planner = new AlertPlanner();
        planner.Next([S("a", SessionKinds.Working)], 0);
        planner.Next([S("a", SessionKinds.Waiting)], 1000); // wyciszony — nikt nie wywołał MarkSounded
        planner.Next([S("a", SessionKinds.Working)], 2000);

        Assert.True(Assert.Single(planner.Next([S("a", SessionKinds.Waiting)], 6000).Raised).Sound);
    }

    [Fact]
    public void A_new_result_is_announced_and_replaces_the_waiting_notification_instead_of_clearing_it()
    {
        var planner = new AlertPlanner();
        planner.Next([S("a", SessionKinds.Waiting)], 0);

        var changes = planner.Next([S("a", SessionKinds.New)], 1000);

        Assert.Equal(AlertKind.NewResult, Assert.Single(changes.Raised).Kind);
        Assert.Empty(changes.Cleared);
    }

    [Fact]
    public void Notifications_are_cleared_when_the_session_stops_waiting_is_seen_or_closes()
    {
        var planner = new AlertPlanner();
        planner.Next([S("a", SessionKinds.Waiting), S("b", SessionKinds.New), S("c", SessionKinds.Waiting), S("d", SessionKinds.Working)], 0);

        var changes = planner.Next([S("a", SessionKinds.Working), S("b", SessionKinds.Idle), S("d", SessionKinds.Idle)], 1000);

        Assert.Equal(["a", "b", "c"], changes.Cleared.Order());
        Assert.Empty(changes.Raised);
    }

    [Fact]
    public void A_session_that_appears_already_waiting_is_announced()
    {
        var planner = new AlertPlanner();
        planner.Next([], 0);

        Assert.Equal("new", Assert.Single(planner.Next([S("new", SessionKinds.Waiting)], 1000).Raised).Session.Id);
    }

    [Fact]
    public void Background_sessions_arriving_with_the_first_agents_listing_are_not_announced()
    {
        // Start widżetu: pierwszy odczyt jeszcze bez listy `claude agents`, kilka sekund później już z nią.
        var planner = new AlertPlanner();
        planner.Next([S("t", SessionKinds.Working)], 0, backgroundKnown: false);

        var changes = planner.Next([S("t", SessionKinds.Working), S("bg", SessionKinds.Waiting, background: true)], 4000);

        Assert.Empty(changes.Raised);
        // Później sesja w tle, która zacznie czekać, jest już normalnie ogłaszana.
        planner.Next([S("t", SessionKinds.Working), S("bg2", SessionKinds.Working, background: true)], 8000);
        Assert.Equal("bg2", Assert.Single(planner.Next([S("t", SessionKinds.Working), S("bg2", SessionKinds.Waiting, background: true)], 12_000).Raised).Session.Id);
    }

    [Fact]
    public void A_failed_agents_listing_neither_clears_nor_re_announces_background_sessions()
    {
        var planner = new AlertPlanner();
        planner.Next([S("bg", SessionKinds.Working, background: true)], 0);
        Assert.Single(planner.Next([S("bg", SessionKinds.Waiting, background: true)], 1000).Raised);

        var outage = planner.Next([], 2000, backgroundKnown: false);
        Assert.Empty(outage.Cleared);

        var back = planner.Next([S("bg", SessionKinds.Waiting, background: true)], 30_000);
        Assert.Empty(back.Raised);
        Assert.Empty(back.Cleared);
    }
}
