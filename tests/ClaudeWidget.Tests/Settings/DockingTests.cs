using ClaudeWidget.Core.Settings;

namespace ClaudeWidget.Tests.Settings;

public class DockingTests
{
    private static readonly Box Area = new(0, 0, 1920, 1032); // 1080 minus pasek zadań
    private const double Shadow = 24;

    [Fact]
    public void A_card_dropped_near_a_side_sticks_to_it()
    {
        Assert.Equal(DockEdge.Right, Docking.Decide(new Box(1760, 300, 148, 560), Area));
        Assert.Equal(DockEdge.Left, Docking.Decide(new Box(15, 300, 148, 560), Area));
    }

    [Fact]
    public void Near_the_top_or_bottom_away_from_the_sides_it_becomes_an_island()
    {
        Assert.Equal(DockEdge.Top, Docking.Decide(new Box(800, 10, 148, 560), Area));
        Assert.Equal(DockEdge.Bottom, Docking.Decide(new Box(800, 1032 - 40 - 5, 104, 40), Area));
    }

    [Fact]
    public void The_top_edge_is_reachable_although_Windows_stops_the_window_with_its_shadow_margin_at_the_screen_top()
    {
        // Okno na y = 0, karta 24 px niżej — bliżej się nie da.
        Assert.Equal(DockEdge.Top, Docking.Decide(new Box(800, 24, 148, 560), Area));
        Assert.Equal(DockEdge.None, Docking.Decide(new Box(800, 60, 148, 560), Area));
    }

    [Fact]
    public void Corners_keep_the_vertical_card_so_old_top_right_placements_do_not_turn_into_an_island()
    {
        Assert.Equal(DockEdge.Right, Docking.Decide(new Box(1764, 8, 148, 560), Area));
        Assert.Equal(DockEdge.Left, Docking.Decide(new Box(8, 1032 - 90 - 8, 34, 90), Area));
    }

    [Fact]
    public void Far_from_every_edge_it_floats_freely()
    {
        Assert.Equal(DockEdge.None, Docking.Decide(new Box(700, 300, 148, 560), Area));
    }

    [Fact]
    public void An_island_touches_the_top_edge_centered_on_its_anchor()
    {
        var (left, top) = Docking.Place(DockEdge.Top, 104, 40, Area, Shadow, 0, 0, anchorX: 500);

        Assert.Equal(500 - 52 - Shadow, left);
        Assert.Equal(-Shadow, top);
    }

    [Fact]
    public void A_bottom_island_sits_on_the_taskbar_and_without_an_anchor_in_the_middle()
    {
        var (left, top) = Docking.Place(DockEdge.Bottom, 104, 40, Area, Shadow, 0, 0, anchorX: null);

        Assert.Equal(960 - 52 - Shadow, left);
        Assert.Equal(1032 - 40 - Shadow, top);
    }

    [Fact]
    public void An_expanding_island_near_a_corner_stays_on_screen()
    {
        var (left, _) = Docking.Place(DockEdge.Top, 498, 40, Area, Shadow, 0, 0, anchorX: 1900);

        Assert.Equal(1920 - 498 - Shadow, left);
    }

    [Fact]
    public void A_right_card_keeps_its_height_and_snaps_to_the_top_or_bottom_only_when_close()
    {
        Assert.Equal((1920 - 8 - 148 - Shadow, 300 - Shadow), Docking.Place(DockEdge.Right, 148, 560, Area, Shadow, 900, 300, null));
        Assert.Equal((1920 - 8 - 148 - Shadow, 8 - Shadow), Docking.Place(DockEdge.Right, 148, 560, Area, Shadow, 900, 24, null));
        Assert.Equal((8 - Shadow, 1032 - 8 - 560 - Shadow), Docking.Place(DockEdge.Left, 148, 560, Area, Shadow, 900, 460, null));
    }

    [Fact]
    public void A_side_card_that_would_hang_off_the_screen_is_pulled_back()
    {
        var (_, top) = Docking.Place(DockEdge.Right, 148, 560, Area, Shadow, 900, 900, null);

        Assert.Equal(1032 - 560 - Shadow, top);
    }

    [Fact]
    public void A_free_card_stays_where_it_is()
    {
        Assert.Equal((700 - Shadow, 300 - Shadow), Docking.Place(DockEdge.None, 148, 560, Area, Shadow, 700, 300, null));
    }

    private static readonly IReadOnlyList<Box> TwoScreens = [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)];

    [Fact]
    public void A_bottom_island_on_a_screen_without_a_taskbar_is_restored_on_that_screen()
    {
        // Wyspa u dołu drugiego ekranu (bez paska zadań): okno sięga marginesem cienia poza ekran.
        Assert.Equal(1, Docking.SavedScreen(DockEdge.Bottom, 3000, 1440 - 40 - Shadow, anchorX: 3100, Shadow, TwoScreens));
    }

    [Fact]
    public void An_island_is_restored_by_its_anchor_even_if_the_saved_left_was_of_the_collapsed_island()
    {
        Assert.Equal(1, Docking.SavedScreen(DockEdge.Top, 1880, -Shadow, anchorX: 4400, Shadow, TwoScreens));
    }

    [Fact]
    public void A_side_card_is_restored_by_its_top_left_corner()
    {
        Assert.Equal(1, Docking.SavedScreen(DockEdge.Left, 1920 + 8 - Shadow, 120 - Shadow, anchorX: null, Shadow, TwoScreens));
        Assert.Equal(0, Docking.SavedScreen(DockEdge.Right, 1920 - 8 - 148 - Shadow, 120 - Shadow, anchorX: null, Shadow, TwoScreens));
    }

    [Fact]
    public void A_disconnected_screen_gives_no_match()
    {
        Assert.Equal(-1, Docking.SavedScreen(DockEdge.Top, 5000, -Shadow, anchorX: 5200, Shadow, TwoScreens));
        Assert.Equal(-1, Docking.SavedScreen(DockEdge.Right, -900, 100, anchorX: null, Shadow, TwoScreens));
    }

    [Theory]
    [InlineData("left", DockEdge.Left)]
    [InlineData("right", DockEdge.Right)]
    [InlineData("top", DockEdge.Top)]
    [InlineData("bottom", DockEdge.Bottom)]
    [InlineData(null, DockEdge.None)]
    [InlineData("middle", DockEdge.None)]
    public void Edges_round_trip_through_the_config_text(string? text, DockEdge edge)
    {
        Assert.Equal(edge, Docking.Parse(text));
        if (edge != DockEdge.None) Assert.Equal(text, Docking.ToText(edge));
    }

    [Fact]
    public void Old_config_files_without_an_edge_mean_a_free_card_and_the_edge_counts_as_placement()
    {
        Assert.Equal(DockEdge.None, new WidgetConfig { Left = 10, Top = 10 }.DockEdge);
        Assert.Equal(
            new WidgetConfig { Volume = 5 }.WithoutPlacement(),
            new WidgetConfig { Volume = 5, Dock = "top", DockAnchor = 300 }.WithoutPlacement());
    }
}
