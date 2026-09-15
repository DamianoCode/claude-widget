using ClaudeWidget.Core;
using ClaudeWidget.Core.Limits;

namespace ClaudeWidget.Tests.Limits;

public sealed class LimitCalculatorTests
{
    private const long Hour = 3_600_000L;

    [Fact]
    public void No_limit_data_gives_empty_view()
    {
        var view = LimitCalculator.GetView(null, LimitWindowKind.FiveHour, null, 0, false);
        Assert.Null(view.Pct);
        Assert.Equal("", view.Note);
        Assert.Null(view.Warn);
    }

    [Fact]
    public void After_reset_time_it_waits_for_new_data()
    {
        var resetsAt = 1000d; // seconds
        var limit = new LimitWindow { Pct = 42, ResetsAt = resetsAt };
        var view = LimitCalculator.GetView(limit, LimitWindowKind.FiveHour, measuredAt: 900_000, nowMs: 1_000_000, panel: false);
        Assert.Equal("po resecie, czekam na nowe dane", view.Note);
        Assert.Null(view.Pct);
    }

    [Theory]
    [InlineData(50, "#D97757", null)]
    [InlineData(75, "#FFB224", "#FFB224")]
    [InlineData(89.9, "#FFB224", "#FFB224")]
    [InlineData(90, "#FF5A4E", "#FF5A4E")]
    [InlineData(99, "#FF5A4E", "#FF5A4E")]
    public void Color_and_warning_follow_percentage_thresholds(double pct, string color, string? warn)
    {
        // Reset far in the future and pct low enough not to trigger the pace forecast on its own.
        var nowMs = 0L;
        var resetMs = 10 * Hour;
        var measuredAt = 1 * Hour; // 10% of the 5h window elapsed, above the noise guard
        var limit = new LimitWindow { Pct = pct, ResetsAt = resetMs / 1000.0 };
        var view = LimitCalculator.GetView(limit, LimitWindowKind.FiveHour, measuredAt, nowMs, panel: false);
        Assert.Equal(color, view.Color);
        Assert.Equal(warn, view.Warn);
    }

    [Fact]
    public void Fast_pace_predicts_exhaustion_before_reset_even_below_75_percent()
    {
        // 60% burned in the first 30 minutes of a 5h window: at this pace it runs out long before reset.
        var resetMs = 5 * Hour;
        var measuredAt = 30 * 60_000L;
        var limit = new LimitWindow { Pct = 60, ResetsAt = resetMs / 1000.0 };
        var view = LimitCalculator.GetView(limit, LimitWindowKind.FiveHour, measuredAt, nowMs: measuredAt, panel: false);
        Assert.Equal("#FFB224", view.Color);
        Assert.Equal("#FFB224", view.Warn);
        Assert.Contains("skończy się ok.", view.Note);
    }

    [Fact]
    public void Card_note_is_short_panel_note_includes_reset()
    {
        var resetMs = 5 * Hour;
        var measuredAt = 30 * 60_000L;
        var limit = new LimitWindow { Pct = 60, ResetsAt = resetMs / 1000.0 };

        var card = LimitCalculator.GetView(limit, LimitWindowKind.FiveHour, measuredAt, nowMs: measuredAt, panel: false);
        var panel = LimitCalculator.GetView(limit, LimitWindowKind.FiveHour, measuredAt, nowMs: measuredAt, panel: true);

        Assert.DoesNotContain("reset", card.Note);
        Assert.Contains("w tym tempie skończy się ok.", panel.Note);
        Assert.Contains("reset", panel.Note);
    }

    [Fact]
    public void Ten_percent_noise_guard_ignores_pace_at_the_very_start_of_the_window()
    {
        // 5 minutes into a 5h window: even 90% used says nothing about the pace yet, but the
        // percentage itself still crosses the red threshold.
        var resetMs = 5 * Hour;
        var measuredAt = 5 * 60_000L;
        var limit = new LimitWindow { Pct = 15, ResetsAt = resetMs / 1000.0 };
        var view = LimitCalculator.GetView(limit, LimitWindowKind.FiveHour, measuredAt, nowMs: measuredAt, panel: false);
        Assert.Equal("#D97757", view.Color);
        Assert.Null(view.Warn);
        Assert.DoesNotContain("skończy się", view.Note);
    }

    [Fact]
    public void Weekly_window_note_includes_day_name_on_card()
    {
        var resetMs = 3 * 24 * Hour;
        var limit = new LimitWindow { Pct = 20, ResetsAt = resetMs / 1000.0 };
        var view = LimitCalculator.GetView(limit, LimitWindowKind.SevenDay, measuredAt: Hour, nowMs: 0, panel: false);
        Assert.StartsWith("reset ", view.Note);
    }
}
