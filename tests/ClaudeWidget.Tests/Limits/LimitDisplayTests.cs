using System.Text.Json;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Limits;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget.Tests.Limits;

public sealed class LimitDisplayTests
{
    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(34, 34, 66)]
    [InlineData(100, 100, 0)]
    [InlineData(104.2, 100, 0)]
    [InlineData(-3, 0, 100)]
    [InlineData(33.5, 34, 66)]
    [InlineData(66.5, 67, 33)]
    [InlineData(23.4, 23, 77)]
    public void Used_and_left_always_add_up_to_100(double pct, double used, double left)
    {
        Assert.Equal(used, LimitDisplay.Shown(pct, LimitDisplayMode.Used));
        Assert.Equal(left, LimitDisplay.Shown(pct, LimitDisplayMode.Left));
    }

    [Theory]
    [InlineData(LimitDisplayMode.Used)]
    [InlineData(LimitDisplayMode.Left)]
    public void Missing_data_stays_missing_in_both_modes(LimitDisplayMode mode)
    {
        Assert.Null(LimitDisplay.Shown(null, mode));
        Assert.Null(LimitDisplay.Shown(double.NaN, mode));
    }

    [Theory]
    [InlineData(null, LimitDisplayMode.Used)]
    [InlineData("used", LimitDisplayMode.Used)]
    [InlineData("left", LimitDisplayMode.Left)]
    [InlineData(" LEFT ", LimitDisplayMode.Left)]
    [InlineData("remaining", LimitDisplayMode.Used)]
    public void Config_value_parses_with_used_as_default(string? value, LimitDisplayMode expected)
    {
        Assert.Equal(expected, LimitDisplay.Parse(value));
        Assert.Equal(expected, new WidgetConfig { LimitDisplay = value }.LimitMode);
    }

    [Fact]
    public void Toggle_round_trips_through_the_config_key()
    {
        var left = LimitDisplay.Toggle(LimitDisplayMode.Used);
        Assert.Equal(LimitDisplayMode.Left, left);
        Assert.Equal(LimitDisplayMode.Left, LimitDisplay.Parse(LimitDisplay.Key(left)));
        Assert.Equal(LimitDisplayMode.Used, LimitDisplay.Toggle(left));
    }

    [Fact]
    public void Words_say_what_the_number_means()
    {
        Assert.Equal("zużyte", LimitDisplay.Word(LimitDisplayMode.Used));
        Assert.Equal("zostało", LimitDisplay.Word(LimitDisplayMode.Left));
    }

    [Fact]
    public void Limits_file_without_source_reads_as_unknown()
    {
        // Statusline ze starszej wersji widżetu nie zapisuje źródła.
        var limits = JsonSerializer.Deserialize("""{"sevenDay":{"pct":34,"resetsAt":1795762800},"updatedAt":1795434294516}""",
            StateJson.Default.AccountLimits);
        Assert.Null(limits!.Source);
        Assert.Equal(34, limits.SevenDay!.Pct);
    }
}

public sealed class LimitDisplayConfigTests
{
    [Fact]
    public void Config_file_round_trips_the_limit_display()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "widget-config.json");
        File.WriteAllText(path, """{ "left": 200, "limitDisplay": "left" }""");
        Assert.Equal(LimitDisplayMode.Left, WidgetConfigStore.Read(path)!.LimitMode);

        WidgetConfigStore.Write(path, new WidgetConfig { LimitDisplay = LimitDisplay.UsedKey });
        Assert.Contains("\"limitDisplay\": \"used\"", File.ReadAllText(path));
    }
}
