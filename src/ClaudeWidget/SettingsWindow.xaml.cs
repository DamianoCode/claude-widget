using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeWidget.Core.Sessions;
using ClaudeWidget.Core.Settings;
using Microsoft.Win32;

namespace ClaudeWidget;

/// <summary>
/// Co okno ustawień potrzebuje od widżetu. Każda zmiana działa i zapisuje się od razu; zmian
/// z samego okna widżet do niego nie odsyła (okno odświeża się samo, gdy trzeba).
/// </summary>
public interface ISettingsHost
{
    WidgetConfig Config { get; }

    /// <summary>Katalog widżetu — w nim folder sounds z dawnymi need.wav i done.wav.</summary>
    string WidgetDir { get; }

    void UpdateConfig(Func<WidgetConfig, WidgetConfig> change);

    void PreviewSound(AlertKind kind, string? choice);

    /// <summary>Dlaczego skrót nie działa (zajęty, wyłączony) albo pusty tekst, gdy działa.</summary>
    string HotkeyProblem { get; }

    void OpenConfigFile();
}

/// <summary>
/// Okno ustawień: dźwięki (wybór pliku, głośność, powtórzenia), powiadomienia i wyciszenie, skrót,
/// krycie i chowanie przy pełnym ekranie. Zmiany z menu albo z ręcznie poprawionego pliku
/// przychodzą przez <see cref="Refresh"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string BrowseTag = "?browse"; // żadna ścieżka tak się nie zaczyna
    // Pusty wybór to dawny sposób: need.wav / done.wav z folderu sounds (albo wbudowany).
    private const string LegacyTag = "";

    private static readonly (int Seconds, string Label)[] RepeatOptions =
    [
        (0, "bez limitu"), (5, "raz na 5 s"), (15, "raz na 15 s"), (30, "raz na 30 s"), (60, "raz na minutę"), (300, "raz na 5 minut"),
    ];

    private readonly ISettingsHost _host;
    private bool _loading;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        foreach (var (duration, label) in MuteOptions.All)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => Change(c => c with { MutedUntil = MuteOptions.Until(duration, DateTimeOffset.Now).ToUnixTimeMilliseconds() }, refresh: true);
            MuteButtons.Children.Add(button);
        }
        var unmute = new Button { Content = "Wyłącz wyciszenie", Name = "Unmute" };
        unmute.Click += (_, _) => Change(c => c with { MutedUntil = null }, refresh: true);
        MuteButtons.Children.Add(unmute);
        Wire();
        Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            var config = _host.Config;
            SoundsCheck.IsChecked = config.SoundsOn;
            FillSounds(WaitingSound, AlertKind.Waiting, config.SoundWaiting);
            FillSounds(DoneSound, AlertKind.NewResult, config.SoundDone);
            VolumeSlider.Value = config.VolumePercent;
            VolumeText.Text = $"{config.VolumePercent}%";
            FillRepeat((int)(config.SoundRepeatMs / 1000));
            NotificationsCheck.IsChecked = config.NotificationsOn;
            var now = DateTimeOffset.Now;
            var muted = config.IsMuted(now.ToUnixTimeMilliseconds());
            MuteStatus.Text = muted
                ? MuteOptions.Describe(DateTimeOffset.FromUnixTimeMilliseconds(config.MutedUntil!.Value).ToLocalTime(), now)
                : "Wyciszenie: wyłączone";
            MuteButtons.Children.OfType<Button>().Single(b => b.Name == "Unmute").IsEnabled = muted;
            HotkeyBox.Text = config.HotkeyText.Length == 0 ? "(brak)" : config.HotkeyText;
            HotkeyStatus.Text = _host.HotkeyProblem;
            HotkeyStatus.Visibility = HotkeyStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            OpacitySlider.Value = config.OpacityPercent;
            OpacityText.Text = $"{config.OpacityPercent}%";
            FullscreenCheck.IsChecked = config.HidesOnFullscreen;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Wire()
    {
        SoundsCheck.Click += (_, _) => _host.UpdateConfig(c => c with { Sounds = SoundsCheck.IsChecked == true });
        NotificationsCheck.Click += (_, _) => _host.UpdateConfig(c => c with { Notifications = NotificationsCheck.IsChecked == true });
        FullscreenCheck.Click += (_, _) => _host.UpdateConfig(c => c with { HideOnFullscreen = FullscreenCheck.IsChecked == true });
        WaitingSound.SelectionChanged += (_, _) => OnSoundChosen(WaitingSound, AlertKind.Waiting);
        DoneSound.SelectionChanged += (_, _) => OnSoundChosen(DoneSound, AlertKind.NewResult);
        WaitingPlay.Click += (_, _) => _host.PreviewSound(AlertKind.Waiting, _host.Config.SoundWaiting);
        DonePlay.Click += (_, _) => _host.PreviewSound(AlertKind.NewResult, _host.Config.SoundDone);
        VolumeSlider.ValueChanged += (_, _) =>
        {
            VolumeText.Text = $"{(int)VolumeSlider.Value}%";
            if (!_loading) _host.UpdateConfig(c => c with { Volume = (int)VolumeSlider.Value });
        };
        // Próbka głośności po puszczeniu suwaka, nie przy każdym kroku.
        VolumeSlider.PreviewMouseLeftButtonUp += (_, _) => _host.PreviewSound(AlertKind.Waiting, _host.Config.SoundWaiting);
        RepeatCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && RepeatCombo.SelectedItem is ComboBoxItem { Tag: int seconds })
            {
                _host.UpdateConfig(c => c with { SoundRepeatSeconds = seconds });
            }
        };
        OpacitySlider.ValueChanged += (_, _) =>
        {
            OpacityText.Text = $"{(int)OpacitySlider.Value}%";
            if (!_loading) _host.UpdateConfig(c => c with { Opacity = (int)OpacitySlider.Value });
        };
        HotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;
        HotkeyBox.GotKeyboardFocus += (_, _) => HotkeyBox.Text = "Naciśnij skrót…";
        HotkeyBox.LostKeyboardFocus += (_, _) => Refresh();
        HotkeyDefault.Click += (_, _) => Change(c => c with { Hotkey = null }, refresh: true);
        HotkeyOff.Click += (_, _) => Change(c => c with { Hotkey = "" }, refresh: true);
        OpenFileButton.Click += (_, _) => _host.OpenConfigFile();
        CloseButton.Click += (_, _) => Close();
    }

    // Odświeżenie po obsłudze zdarzenia — nie przebudowuje się listy w trakcie jej SelectionChanged.
    private void Change(Func<WidgetConfig, WidgetConfig> change, bool refresh = false)
    {
        _host.UpdateConfig(change);
        if (refresh) Dispatcher.BeginInvoke(Refresh);
    }

    private void FillSounds(ComboBox combo, AlertKind kind, string? choice)
    {
        combo.Items.Clear();
        var legacy = Path.Combine(AlertSounds.CustomDir(_host.WidgetDir), AlertSounds.FileName(kind));
        combo.Items.Add(Item("Wbudowany", AlertSounds.Builtin));
        combo.Items.Add(Item("Cisza", AlertSounds.None));
        if (File.Exists(legacy)) combo.Items.Add(Item($"Własny: sounds\\{AlertSounds.FileName(kind)}", LegacyTag));
        var chosenPath = choice is null or AlertSounds.Builtin or AlertSounds.None || choice.Length == 0 ? null : choice;
        if (chosenPath is not null && !SystemSounds().Contains(chosenPath, StringComparer.OrdinalIgnoreCase))
        {
            combo.Items.Add(Item($"Plik: {Path.GetFileName(chosenPath)}{(File.Exists(chosenPath) ? "" : " (brak pliku)")}", chosenPath, chosenPath));
        }
        foreach (var path in SystemSounds())
        {
            combo.Items.Add(Item($"Windows: {Path.GetFileNameWithoutExtension(path)}", path, path));
        }
        combo.Items.Add(Item("Wybierz plik…", BrowseTag));

        var selected = choice switch
        {
            null or "" => File.Exists(legacy) ? LegacyTag : AlertSounds.Builtin,
            _ => choice,
        };
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string)item.Tag, selected, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSoundChosen(ComboBox combo, AlertKind kind)
    {
        if (_loading || combo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var choice = tag;
        if (tag == BrowseTag)
        {
            var dialog = new OpenFileDialog
            {
                Title = kind == AlertKind.Waiting ? "Dźwięk: sesja czeka" : "Dźwięk: nowy wynik",
                Filter = "Dźwięki|" + string.Join(';', AlertSounds.Extensions.Select(e => "*" + e)),
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media"),
            };
            if (dialog.ShowDialog(this) != true)
            {
                Dispatcher.BeginInvoke(Refresh);
                return;
            }
            choice = dialog.FileName;
        }
        var value = choice.Length == 0 ? null : choice;
        // Wybrany plik spoza listy dochodzi do niej jako „Plik: …”.
        Change(c => kind == AlertKind.Waiting ? c with { SoundWaiting = value } : c with { SoundDone = value }, refresh: tag == BrowseTag);
        _host.PreviewSound(kind, value);
    }

    private void FillRepeat(int seconds)
    {
        RepeatCombo.Items.Clear();
        var options = RepeatOptions.Any(o => o.Seconds == seconds) ? RepeatOptions : [.. RepeatOptions, (seconds, $"raz na {seconds} s")];
        foreach (var (value, label) in options.OrderBy(o => o.Seconds))
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            RepeatCombo.Items.Add(item);
            if (value == seconds) RepeatCombo.SelectedItem = item;
        }
    }

    // Skrót zapisuje się od razu po naciśnięciu pełnej kombinacji; Esc przerywa.
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape || key == Key.Tab)
        {
            Keyboard.ClearFocus();
            CloseButton.Focus();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }
        uint modifiers = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= Hotkey.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= Hotkey.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= Hotkey.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= Hotkey.Win;
        var text = Hotkey.Format(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (text is null)
        {
            HotkeyBox.Text = "Ctrl/Alt/Shift/Win + litera, cyfra albo F1–F24";
            return;
        }
        _host.UpdateConfig(c => c with { Hotkey = text });
        Keyboard.ClearFocus(); // LostKeyboardFocus odświeża okno, także stan skrótu
        CloseButton.Focus();
    }

    private static ComboBoxItem Item(string label, string tag, string? tooltip = null) =>
        new() { Content = label, Tag = tag, ToolTip = tooltip };

    private static List<string>? _systemSounds;

    private static List<string> SystemSounds()
    {
        if (_systemSounds is not null) return _systemSounds;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            _systemSounds = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.wav").Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _systemSounds = [];
        }
        return _systemSounds;
    }
}
