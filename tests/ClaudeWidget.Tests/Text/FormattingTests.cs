using ClaudeWidget.Core.Text;

namespace ClaudeWidget.Tests.Text;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(59_000, "59 s")]
    [InlineData(60_000, "1 min")]
    [InlineData(3599_000, "59 min")]
    [InlineData(3600_000, "1 h")]
    [InlineData(3660_000, "1 h 1 min")]
    [InlineData(86399_000, "23 h 59 min")]
    [InlineData(86400_000, "1 d")]
    [InlineData(172800_000, "2 d")]
    public void FormatSpan_matches_widget_ps1(double ms, string expected) => Assert.Equal(expected, Formatting.FormatSpan(ms));

    [Fact]
    public void FormatSpan_clamps_negative_to_zero() => Assert.Equal("0 s", Formatting.FormatSpan(-500));

    [Theory]
    [InlineData(1, "1 sesja")]
    [InlineData(2, "2 sesje")]
    [InlineData(4, "4 sesje")]
    [InlineData(5, "5 sesji")]
    [InlineData(11, "11 sesji")]
    [InlineData(12, "12 sesji")]
    [InlineData(14, "14 sesji")]
    [InlineData(22, "22 sesje")]
    [InlineData(0, "0 sesji")]
    public void FormatSessions_uses_polish_plural(int count, string expected) => Assert.Equal(expected, Formatting.FormatSessions(count));

    [Theory]
    [InlineData(999, "1 tys.")]
    [InlineData(1500, "2 tys.")]
    [InlineData(150000, "150 tys.")]
    [InlineData(1_000_000, "1 mln")]
    [InlineData(1_200_000, "1.2 mln")]
    public void FormatTokens_rounds_appropriately(double tokens, string expected) => Assert.Equal(expected, Formatting.FormatTokens(tokens));

    [Fact]
    public void GetWaitSpan_shows_under_a_minute_as_placeholder()
    {
        Assert.Equal("<1 min", Formatting.GetWaitSpan(since: 0, nowMs: 59_000));
        Assert.Equal("1 min", Formatting.GetWaitSpan(since: 0, nowMs: 60_000));
    }

    [Fact]
    public void GetFreshnessText_without_data_says_so() => Assert.Equal("brak danych o limitach", Formatting.GetFreshnessText(null));

    [Fact]
    public void GetFreshnessText_today_shows_time_only()
    {
        var now = DateTimeOffset.Now;
        var text = Formatting.GetFreshnessText(now.ToUnixTimeMilliseconds());
        Assert.StartsWith("stan z ", text);
        Assert.DoesNotContain(".", text);
    }

    [Fact]
    public void GetFreshnessText_older_day_includes_date()
    {
        var past = DateTimeOffset.Now.AddDays(-2);
        var text = Formatting.GetFreshnessText(past.ToUnixTimeMilliseconds());
        Assert.Contains(".", text);
    }
}
