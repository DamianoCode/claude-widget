using ClaudeWidget.Core.Sessions;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget.Tests.Settings;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+K", Hotkey.Control | Hotkey.Alt, 'K')]
    [InlineData("alt + ctrl + k", Hotkey.Control | Hotkey.Alt, 'K')]
    [InlineData("Win+Shift+7", Hotkey.Win | Hotkey.Shift, '7')]
    [InlineData("Control+F12", Hotkey.Control, 0x7B)]
    [InlineData("Ctrl+F24", Hotkey.Control, 0x87)]
    public void Parses_modifiers_and_a_key(string text, uint modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("F5")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Space")]
    [InlineData("Ctrl+ż")]
    public void Rejects_hotkeys_without_a_modifier_or_with_an_unknown_key(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Formats_modifiers_in_a_fixed_order_so_text_round_trips()
    {
        var text = Hotkey.Format(Hotkey.Win | Hotkey.Shift | Hotkey.Alt | Hotkey.Control, 0x70);

        Assert.Equal("Ctrl+Alt+Shift+Win+F1", text);
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(text, hotkey.ToString());
    }

    [Fact]
    public void Keys_that_cannot_be_written_down_give_no_text()
    {
        Assert.Null(Hotkey.Format(Hotkey.Control, 0x20)); // spacja
        Assert.Null(Hotkey.Format(0, 'K'));
    }
}

public class AlertSoundsTests
{
    private const string WidgetDir = @"C:\u\.claude\widget";
    private const string AppDir = @"C:\app";
    private static readonly string Builtin = Path.Combine(AppDir, "Assets", "Sounds", "need.wav");
    private static readonly string Legacy = Path.Combine(WidgetDir, "sounds", "need.wav");

    [Fact]
    public void Without_a_choice_the_file_in_the_sounds_folder_replaces_the_builtin_one()
    {
        Assert.Equal(Legacy, AlertSounds.Resolve(null, AlertKind.Waiting, WidgetDir, AppDir, path => path == Legacy));
        Assert.Equal(Builtin, AlertSounds.Resolve(null, AlertKind.Waiting, WidgetDir, AppDir, _ => false));
    }

    [Fact]
    public void Builtin_ignores_the_sounds_folder_and_none_means_silence()
    {
        Assert.Equal(Builtin, AlertSounds.Resolve(AlertSounds.Builtin, AlertKind.Waiting, WidgetDir, AppDir, _ => true));
        Assert.Null(AlertSounds.Resolve(AlertSounds.None, AlertKind.Waiting, WidgetDir, AppDir, _ => true));
    }

    [Fact]
    public void A_chosen_file_plays_while_it_exists_and_falls_back_to_the_builtin_one_when_gone()
    {
        const string chosen = @"D:\dzwieki\ping.mp3";

        Assert.Equal(chosen, AlertSounds.Resolve(chosen, AlertKind.Waiting, WidgetDir, AppDir, path => path == chosen));
        Assert.Equal(Builtin, AlertSounds.Resolve(chosen, AlertKind.Waiting, WidgetDir, AppDir, _ => false));
    }

    [Fact]
    public void Each_kind_has_its_own_builtin_file()
    {
        Assert.EndsWith("done.wav", AlertSounds.Resolve(AlertSounds.Builtin, AlertKind.NewResult, WidgetDir, AppDir, _ => true));
    }
}

public class MuteOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 14, 5, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Mute_durations_end_at_the_expected_time()
    {
        Assert.Equal(Now.AddMinutes(30), MuteOptions.Until(MuteDuration.HalfHour, Now));
        Assert.Equal(Now.AddHours(1), MuteOptions.Until(MuteDuration.OneHour, Now));
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.FromHours(2)), MuteOptions.Until(MuteDuration.UntilTomorrow, Now));
    }

    [Fact]
    public void Describes_the_end_of_muting()
    {
        Assert.Equal("Wyciszone do 14:35", MuteOptions.Describe(Now.AddMinutes(30), Now));
        Assert.Equal("Wyciszone do jutra", MuteOptions.Describe(MuteOptions.Until(MuteDuration.UntilTomorrow, Now), Now));
        Assert.Equal("Wyciszone do 18.09 01:00", MuteOptions.Describe(Now.AddHours(11).AddMinutes(-5), Now));
    }
}

public class WidgetConfigTests
{
    [Fact]
    public void Missing_entries_mean_defaults()
    {
        var config = new WidgetConfig();

        Assert.True(config.SoundsOn);
        Assert.True(config.NotificationsOn);
        Assert.Equal(100, config.VolumePercent);
        Assert.Equal(15_000, config.SoundRepeatMs);
        Assert.Equal("Ctrl+Alt+K", config.HotkeyText);
        Assert.True(config.HidesOnFullscreen);
        Assert.Equal(100, config.OpacityPercent);
        Assert.False(config.IsMuted(0));
    }

    [Fact]
    public void Values_out_of_range_are_clamped()
    {
        var config = new WidgetConfig { Volume = 250, Opacity = 5, SoundRepeatSeconds = -3 };

        Assert.Equal(100, config.VolumePercent);
        Assert.Equal(WidgetConfig.MinOpacity, config.OpacityPercent);
        Assert.Equal(0, config.SoundRepeatMs);
    }

    [Fact]
    public void Muting_lasts_until_the_given_moment()
    {
        var config = new WidgetConfig { MutedUntil = 1000 };

        Assert.True(config.IsMuted(999));
        Assert.False(config.IsMuted(1000));
    }

    [Fact]
    public void Round_trips_through_the_file_without_writing_computed_or_empty_values()
    {
        using var dir = new TempDir();
        var path = dir.File("widget-config.json");
        var config = new WidgetConfig { Left = 10, Size = "full", SoundWaiting = @"C:\x.mp3", Volume = 40, Hotkey = "" };

        WidgetConfigStore.Write(path, config);
        var text = File.ReadAllText(path);

        Assert.Equal(config, WidgetConfigStore.Read(path));
        Assert.Contains("\"soundWaiting\"", text);
        Assert.Contains("\"hotkey\": \"\"", text);
        Assert.DoesNotContain("soundsOn", text);
        Assert.DoesNotContain("\"top\"", text);
    }

    [Fact]
    public void Old_files_with_only_position_and_toggles_still_load()
    {
        using var dir = new TempDir();
        var path = dir.File("widget-config.json");
        File.WriteAllText(path, """{"left":1.5,"top":2,"size":"mini","sounds":false,"notifications":true}""");

        var config = WidgetConfigStore.Read(path);

        Assert.NotNull(config);
        Assert.False(config.SoundsOn);
        Assert.Null(config.SoundWaiting);
        Assert.Equal(1.5, config.Left);
    }

    [Fact]
    public void Placement_does_not_count_when_comparing_settings()
    {
        var a = new WidgetConfig { Left = 1, Top = 2, Size = "mini", Volume = 50 };
        var b = new WidgetConfig { Left = 9, Size = "full", Volume = 50 };

        Assert.Equal(a.WithoutPlacement(), b.WithoutPlacement());
        Assert.NotEqual(a.WithoutPlacement(), (b with { Volume = 60 }).WithoutPlacement());
    }
}
