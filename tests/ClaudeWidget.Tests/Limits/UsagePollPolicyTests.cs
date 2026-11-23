using ClaudeWidget.Core;
using ClaudeWidget.Core.Limits;

namespace ClaudeWidget.Tests.Limits;

public sealed class UsagePollPolicyTests
{
    private const long Now = 1_795_000_000_000;
    private const long Minute = 60_000;

    private static AccountLimits Limits(long updatedAt, double? fiveHourResetSeconds = null) => new()
    {
        FiveHour = new LimitWindow { Pct = 10, ResetsAt = fiveHourResetSeconds },
        SevenDay = new LimitWindow { Pct = 5, ResetsAt = (Now + 3 * 86_400_000) / 1000.0 },
        UpdatedAt = updatedAt,
    };

    [Fact]
    public void Recent_reading_is_fresh_and_old_one_is_not()
    {
        Assert.True(UsagePollPolicy.IsFresh(Limits(Now - 1 * Minute), Now));
        Assert.False(UsagePollPolicy.IsFresh(Limits(Now - 5 * Minute), Now));
        Assert.False(UsagePollPolicy.IsFresh(null, Now));
        Assert.False(UsagePollPolicy.IsFresh(new AccountLimits(), Now));
    }

    [Fact]
    public void Full_server_reading_is_refreshed_even_while_the_statusline_is_fresh()
    {
        var statusline = Limits(Now - 1 * Minute);
        ServerLimits Server(long fetchedAt, params ScopedLimit[] scoped) =>
            new() { FetchedAt = fetchedAt, Scoped = scoped.Length > 0 ? [.. scoped] : null };

        Assert.True(UsagePollPolicy.IsFresh(statusline, Server(Now - 14 * Minute), Now));
        Assert.False(UsagePollPolicy.IsFresh(statusline, Server(Now - 15 * Minute), Now));
        // Bez pełnego odczytu nie wiadomo, które limity obowiązują — pytamy od razu.
        Assert.False(UsagePollPolicy.IsFresh(statusline, null, Now));
        // Stary limits.json i tak wymaga zapytania.
        Assert.False(UsagePollPolicy.IsFresh(Limits(Now - 6 * Minute), Server(Now - 1 * Minute), Now));

        // Reset limitu modelu, który minął po odczycie — jego procent jest nieaktualny.
        var reset = new ScopedLimit { Label = "Fable", Group = "weekly", Pct = 80, ResetsAt = (Now - 30_000) / 1000.0 };
        Assert.False(UsagePollPolicy.IsFresh(statusline, Server(Now - 2 * Minute, reset), Now));
        Assert.True(UsagePollPolicy.IsFresh(statusline, Server(Now - 10_000, reset), Now));
    }

    [Fact]
    public void Reading_from_before_a_reset_that_passed_is_not_fresh()
    {
        var resetSeconds = (Now - 30_000) / 1000.0;
        Assert.False(UsagePollPolicy.IsFresh(Limits(Now - 1 * Minute, resetSeconds), Now));
        Assert.True(UsagePollPolicy.IsFresh(Limits(Now - 10_000, resetSeconds), Now));
    }

    [Fact]
    public void Server_reading_deadline_and_model_resets_bring_the_next_poll_forward()
    {
        // Statusline odświeża limits.json, więc jego termin ciągle się odsuwa — decyduje wiek odczytu serwera.
        var server = new ServerLimits { FetchedAt = Now - 14 * Minute };
        Assert.Equal(TimeSpan.FromMinutes(1), UsagePollPolicy.NextDelay(UsagePollOutcome.Fresh, Limits(Now - 1 * Minute), Now, server: server));

        // Reset limitu modelu za 2 minuty — pytamy tuż po nim.
        var scoped = new ServerLimits
        {
            FetchedAt = Now - 1 * Minute,
            Scoped = [new ScopedLimit { Label = "Fable", Group = "weekly", Pct = 90, ResetsAt = (Now + 2 * Minute) / 1000.0 }],
        };
        Assert.Equal(TimeSpan.FromMinutes(2) + UsagePollPolicy.AfterReset,
            UsagePollPolicy.NextDelay(UsagePollOutcome.Fresh, Limits(Now), Now, server: scoped));
    }

    [Fact]
    public void After_success_waits_until_reading_ages()
    {
        Assert.Equal(UsagePollPolicy.Interval, UsagePollPolicy.NextDelay(UsagePollOutcome.Ok, Limits(Now), Now));
        Assert.Equal(TimeSpan.FromMinutes(3), UsagePollPolicy.NextDelay(UsagePollOutcome.Fresh, Limits(Now - 2 * Minute), Now));
    }

    [Fact]
    public void Polls_right_after_an_upcoming_reset()
    {
        var resetSeconds = (Now + 2 * Minute) / 1000.0;
        var delay = UsagePollPolicy.NextDelay(UsagePollOutcome.Ok, Limits(Now, resetSeconds), Now);
        Assert.Equal(TimeSpan.FromMinutes(2) + UsagePollPolicy.AfterReset, delay);
    }

    [Fact]
    public void Never_polls_more_often_than_every_30_seconds()
    {
        var delay = UsagePollPolicy.NextDelay(UsagePollOutcome.Fresh, Limits(Now - 5 * Minute + 1000), Now);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void Missing_token_only_rechecks_the_local_file()
    {
        Assert.Equal(UsagePollPolicy.TokenRecheck, UsagePollPolicy.NextDelay(UsagePollOutcome.WaitingForToken, null, Now));
    }

    [Fact]
    public void Rate_limit_honours_retry_after_within_bounds()
    {
        Assert.Equal(TimeSpan.FromMinutes(7), UsagePollPolicy.NextDelay(UsagePollOutcome.RateLimited, null, Now, retryAfter: TimeSpan.FromMinutes(7)));
        Assert.Equal(UsagePollPolicy.RateLimitDefault, UsagePollPolicy.NextDelay(UsagePollOutcome.RateLimited, null, Now));
        Assert.Equal(TimeSpan.FromMinutes(1), UsagePollPolicy.NextDelay(UsagePollOutcome.RateLimited, null, Now, retryAfter: TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromHours(1), UsagePollPolicy.NextDelay(UsagePollOutcome.RateLimited, null, Now, retryAfter: TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(6, 30)]
    [InlineData(50, 30)]
    public void Failures_back_off_exponentially(int failures, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), UsagePollPolicy.NextDelay(UsagePollOutcome.Failed, null, Now, failures));
    }
}
