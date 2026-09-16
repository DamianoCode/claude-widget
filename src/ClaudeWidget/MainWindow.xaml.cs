using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeWidget.Core;
using ClaudeWidget.Core.Agents;
using ClaudeWidget.Core.Ide;
using ClaudeWidget.Core.Settings;
using ClaudeWidget.Core.Limits;
using ClaudeWidget.Core.Sessions;
using ClaudeWidget.Core.Text;
using ClaudeWidget.Native;
using MenuItem = System.Windows.Controls.MenuItem;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using ToolStripSeparator = System.Windows.Forms.ToolStripSeparator;

namespace ClaudeWidget;

/// <summary>
/// Widżet Claude Code: sygnalizator stanu sesji i limity konta. Port widget.ps1 na WPF/.NET —
/// zdarzeniowy (FileSystemWatcher zamiast odpytywania co 200 ms), bez procesów node.
/// </summary>
public partial class MainWindow : Window
{
    // Rozmiary z widget.ps1 (patrz komentarze przy zmiennych $CardInner itd.).
    private const double CardInner = 122;
    private const double PanelInner = 306;
    private const double ShadowMargin = 24;
    private const double SnapDistance = 24;
    private const long SeenAfterMs = 3000;
    private const uint HotkeyModifiers = 3; // MOD_ALT | MOD_CONTROL
    private const uint HotkeyKeyCode = 0x4B; // K
    private const string HotkeyLabel = "Ctrl+Alt+K";
    private const int AgentsIdleSeconds = 20;
    private const string IdleBorder = "#47FFFFFF";
    private static readonly TimeSpan AgentsActiveInterval = TimeSpan.FromSeconds(4);

    private static readonly (string Key, string On, string Off)[] LightPalette =
    [
        ("czeka", "#FF5A4E", "#3D1D1A"),
        ("pracuje", "#FFB224", "#3B2E15"),
        ("gotowe", "#3DD68C", "#173324"),
    ];

    private readonly WidgetPaths _paths;
    private readonly SessionAggregator _aggregator;
    private readonly HostResolver _hostResolver = new();
    private readonly VsCodeBridge _vsCode;
    private readonly AlertPlanner _alerts = new();
    private readonly SessionNotifier _notifier;
    private MenuItem _soundsItem = null!, _notificationsItem = null!;
    private ToolStripMenuItem _traySounds = null!, _trayNotifications = null!;
    private readonly UpdateService _updates = new();
    private readonly Dictionary<string, RadialGradientBrush> _litFill = [];
    private readonly Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> _fullGlow = [];
    private readonly Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> _miniGlow = [];
    private readonly List<(Dictionary<string, Ellipse> Ellipses, Dictionary<string, TextBlock> Counts, Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> Glow)> _lightSets = [];
    private readonly Dictionary<string, long> _dwell = [];

    private MeterControl _m5h = null!, _mWeek = null!, _mContext = null!, _p5h = null!, _pWeek = null!;
    private Style _rowStyle = null!, _waitingRowStyle = null!;
    private MenuItem _sizeItem = null!;
    private ToolStripMenuItem _trayShow = null!, _traySize = null!, _trayAutostart = null!, _trayUpdateItem = null!;
    private NotifyIcon _tray = null!;
    private Icon? _trayIcon;
    private string _trayIconKey = "";
    private GlobalHotkey? _hotkey;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounceTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _agentsTimer = new() { Interval = AgentsActiveInterval };
    private readonly DispatcherTimer _cleanupTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private IReadOnlyList<SessionInfo> _sessions = [];
    private AgentsSnapshot? _agentsSnapshot;
    private IReadOnlyList<AgentSession> _agentsPrevious = [];
    private bool _agentsRunning;
    private DateTime _agentsStartedAt = DateTime.MinValue;
    private string _size = "mini";
    private IntPtr _hwnd, _lastForeground;
    private bool _pinned, _userHidden, _fullscreenHidden;
    private string _panelSignature = "";
    private string _lastError = "";
    private readonly string[] _restartArgs;

    // startupArgs jest tu tylko dla symetrii z App.Main — restart zawsze przekazuje własny,
    // znormalizowany --state-dir (patrz niżej), więc niezależnie od tego, jak widżet wystartował,
    // trafi po aktualizacji w ten sam katalog stanu.
    public MainWindow(WidgetPaths paths, string[] startupArgs)
    {
        _paths = paths;
        _restartArgs = ["--state-dir", _paths.StateDir];
        _aggregator = new SessionAggregator(_paths, new SystemProcessProbe());
        _aggregator.SessionClosed += _hostResolver.Forget;
        _vsCode = VsCodeBridge.For(_paths, IsProcessAlive);
        _notifier = new SessionNotifier(_paths, _updates.IsInstalled, message => WidgetLog.Write(_paths, message), OnNotificationActivated);

        InitializeComponent();
        BuildPalette();
        BuildMeters();
        BuildMenus();
        WireEvents();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // --- kolory i światła --------------------------------------------------------------------

    private void BuildPalette()
    {
        _rowStyle = (Style)FindResource("Row");
        _waitingRowStyle = (Style)FindResource("WaitingRow");

        foreach (var (key, on, _) in LightPalette)
        {
            var color = Brushes.Color(on);
            _litFill[key] = Brushes.LitFill(color);
            _fullGlow[key] = Brushes.Glow(color, 26, 0.9);
            _miniGlow[key] = Brushes.Glow(color, 14, 0.9);
        }

        _lightSets.Add((
            new() { ["czeka"] = LightCzeka, ["pracuje"] = LightPracuje, ["gotowe"] = LightGotowe },
            new() { ["czeka"] = CountCzeka, ["pracuje"] = CountPracuje, ["gotowe"] = CountGotowe },
            _fullGlow));
        _lightSets.Add((
            new() { ["czeka"] = MiniCzeka, ["pracuje"] = MiniPracuje, ["gotowe"] = MiniGotowe },
            new() { ["czeka"] = MiniCountCzeka, ["pracuje"] = MiniCountPracuje, ["gotowe"] = MiniCountGotowe },
            _miniGlow));
    }

    private void BuildMeters()
    {
        _m5h = new MeterControl("Limit 5 h", CardInner, 11, "#A6A6A6", 10);
        _mWeek = new MeterControl("Tydzień", CardInner, 11, "#A6A6A6", 10);
        _mContext = new MeterControl("Kontekst sesji", CardInner, 11, "#A6A6A6", 8);
        foreach (var meter in new[] { _m5h, _mWeek, _mContext }) Meters.Children.Add(meter.Root);

        _p5h = new MeterControl("Limit 5 h", PanelInner, 12, "#C8C8C8", 10);
        _pWeek = new MeterControl("Limit tygodniowy", PanelInner, 12, "#C8C8C8", 0);
        foreach (var meter in new[] { _p5h, _pWeek }) PanelLimits.Children.Add(meter.Root);
    }

    // --- odświeżanie widoku -------------------------------------------------------------------

    private long NowMs() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private void UpdateView()
    {
        var nowMs = NowMs();
        _sessions = _aggregator.GetSessions(nowMs, _agentsSnapshot);
        AnnounceChanges(nowMs);
        var limits = JsonStore.Read(_paths.LimitsFile, StateJson.Default.AccountLimits);
        // Bez czasu pomiaru (0 z pliku bez updatedAt) nie ma ani świeżości, ani prognozy tempa.
        long? measuredAt = limits is { UpdatedAt: > 0 } ? limits.UpdatedAt : null;
        var fiveView = LimitCalculator.GetView(limits?.FiveHour, LimitWindowKind.FiveHour, measuredAt, nowMs, panel: false);
        var weekView = LimitCalculator.GetView(limits?.SevenDay, LimitWindowKind.SevenDay, measuredAt, nowMs, panel: false);
        var focus = _sessions.Count > 0 ? _sessions[0] : null;

        var counts = new Dictionary<string, int> { ["czeka"] = 0, ["pracuje"] = 0, ["gotowe"] = 0 };
        foreach (var session in _sessions)
        {
            var light = SessionKindCatalog.Kinds[session.Kind].Light;
            if (light is not null) counts[light]++;
        }
        foreach (var (key, _, _) in LightPalette)
        {
            foreach (var set in _lightSets)
            {
                var ellipse = set.Ellipses[key];
                var count = set.Counts[key];
                if (counts[key] > 0)
                {
                    ellipse.Fill = _litFill[key];
                    ellipse.Effect = set.Glow[key];
                    count.Text = counts[key].ToString();
                    count.Visibility = Visibility.Visible;
                }
                else
                {
                    ellipse.Fill = Brushes.Brush(LightPalette.First(l => l.Key == key).Off);
                    ellipse.Effect = null;
                    count.Visibility = Visibility.Collapsed;
                }
            }
        }
        SetPulse(counts["czeka"] > 0);

        if (focus is not null)
        {
            var kindInfo = SessionKindCatalog.Kinds[focus.Kind];
            StatusLabel.Text = kindInfo.Label;
            StatusLabel.Foreground = Brushes.Brush(focus.Kind == SessionKinds.Idle ? "#8A8A8A" : kindInfo.Color);
            StatusDetail.Text = focus.Kind == SessionKinds.Idle ? "wyniki przejrzane" : SessionFormatting.GetDetail(focus, nowMs);
            SessionLine.Text = $"{focus.Project} · {Formatting.FormatSessions(_sessions.Count)}";
        }
        else
        {
            StatusLabel.Text = "Brak sesji";
            StatusLabel.Foreground = Brushes.Brush("#8A8A8A");
            StatusDetail.Text = "uruchom Claude Code";
            SessionLine.Text = "";
        }

        _m5h.Set(fiveView.Pct, fiveView.Note, fiveView.Color);
        _mWeek.Set(weekView.Pct, weekView.Note, weekView.Color);
        var contextPct = focus?.ContextPct;
        _mContext.Set(contextPct, SessionFormatting.GetContextNote(focus), contextPct >= 80 ? "#FFB224" : "#BDBDBD");
        Freshness.Text = Formatting.GetFreshnessText(measuredAt);

        // Obramowanie ostrzega przed końcem limitu także w widoku mini, bez najeżdżania.
        var warn = fiveView.Warn == "#FF5A4E" || weekView.Warn == "#FF5A4E" ? "#FF5A4E" : fiveView.Warn ?? weekView.Warn;
        // Bez ostrzeżenia ramka zostaje wyraźna: przy zgaszonych światłach ciemna karta ginęła na ciemnej tapecie.
        var borderBrush = Brushes.Brush(warn is not null ? "#B3" + warn[1..] : IdleBorder);
        Card.BorderBrush = borderBrush;
        Mini.BorderBrush = borderBrush;
        Mini.BorderThickness = new Thickness(warn is not null ? 1.5 : 1);

        UpdateTray(counts);

        if (Panel.IsOpen)
        {
            var panelFive = LimitCalculator.GetView(limits?.FiveHour, LimitWindowKind.FiveHour, measuredAt, nowMs, panel: true);
            var panelWeek = LimitCalculator.GetView(limits?.SevenDay, LimitWindowKind.SevenDay, measuredAt, nowMs, panel: true);
            UpdatePanel(_sessions, panelFive, panelWeek, measuredAt, nowMs);
        }
    }

    private void UpdatePanel(IReadOnlyList<SessionInfo> sessions, LimitView fiveView, LimitView weekView, long? measuredAt, long nowMs)
    {
        PanelCount.Text = sessions.Count.ToString();
        PanelFreshness.Text = Formatting.GetFreshnessText(measuredAt);

        // Wiersze buduje się od nowa tylko, gdy zmieni się to, co pokazują — inaczej podświetlenie
        // pod kursorem migałoby co sekundę.
        var signature = string.Join('\n', sessions.Select(s =>
            $"{s.Id}|{s.Kind}|{SessionFormatting.GetSessionMeta(s, nowMs)}|{s.Name}|{s.ContextPct}|{s.Summary}"));
        if (signature != _panelSignature)
        {
            _panelSignature = signature;
            SessionList.Children.Clear();
            if (sessions.Count == 0)
            {
                SessionList.Children.Add(new TextBlock { Text = "Brak otwartych sesji", FontSize = 12, Foreground = Brushes.Brush("#A6A6A6") });
            }
            foreach (var session in sessions)
            {
                SessionList.Children.Add(SessionRowFactory.Create(session, nowMs, _rowStyle, _waitingRowStyle, OnSessionRowClicked));
            }
        }
        _p5h.Set(fiveView.Pct, fiveView.Note, fiveView.Color);
        _pWeek.Set(weekView.Pct, weekView.Note, weekView.Color);
    }

    private async void OnSessionRowClicked(SessionInfo session)
    {
        await Safely2("przejście do terminala", () => ShowSessionTerminalAsync(session));
        ClosePanel();
        Safely("odświeżanie", UpdateView);
    }

    private bool _pulsing;

    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        foreach (var light in new[] { LightCzeka, MiniCzeka })
        {
            if (on)
            {
                var pulse = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.45, new Duration(TimeSpan.FromMilliseconds(900)))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                };
                // Przezroczyste okno z poświatą WPF rysuje programowo, całe przy każdej klatce: w domyślnych
                // 60 kl./s sam puls zjadał ~25% rdzenia. Wolnemu pulsowi wystarcza kilkanaście klatek.
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pulse, 15);
                light.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                light.BeginAnimation(OpacityProperty, null);
                light.Opacity = 1;
            }
        }
    }

    // --- przejście do terminala sesji ---------------------------------------------------------

    private void SetSeen(string id)
    {
        try
        {
            JsonStore.Write(_paths.SessionFile(id, SessionFileKind.Seen)!, new SeenMarker { SeenAt = NowMs() }, StateJson.Default.SeenMarker);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WidgetLog.Write(_paths, $"znacznik przejrzenia: {error.Message}");
        }
    }

    private async Task ShowSessionTerminalAsync(SessionInfo session)
    {
        SetSeen(session.Id);
        if (session.IsBackground)
        {
            OpenBackgroundSession(session);
            return;
        }
        if (session.Pid == 0) return;
        // Zapytanie WMI o proces-hosta trwa czasem setki ms; idzie w tle, żeby klik albo skrót
        // klawiszowy nie zamroziły okna.
        var host = await _hostResolver.ResolveAsync(session.Pid);
        // Okno konsoli sesji wskazuje dokładnie okno terminala — także zminimalizowane i przy kilku
        // oknach jednego Windows Terminal, gdzie tytuł mówi tylko o aktywnej karcie. Liczy się je od
        // nowa przy każdym kliknięciu, bo kartę da się przenieść do innego okna.
        var consoleWindow = await Task.Run(() => NativeMethods.ConsoleOwnerWindow(session.Pid));
        _hostResolver.UpdateWindow(session.Pid, consoleWindow);
        // Terminal w VS Code: okno z tym terminalem zna rozszerzenie-most — jego folder idzie na
        // początek wskazówek, a po wyciągnięciu okna rozszerzenie samo przełącza na terminal sesji.
        var bridgeWindow = VsCodeBridge.OwnerOf(_vsCode.ReadWindows(), host.Chain);
        var target = consoleWindow != IntPtr.Zero ? consoleWindow
            : host.HostPid != 0 ? NativeMethods.FindWindowOf((uint)host.HostPid, TitleHints(session, host.HostPid, bridgeWindow))
            : IntPtr.Zero;
        if (target == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(target)) NativeMethods.ShowWindow(target, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(target);
        if (bridgeWindow is not null) _vsCode.RequestFocus(host.Chain, NowMs());
    }

    // Pliki ~/.claude/ide/*.lock czyta się przy każdym użyciu: to kilka małych plików, a okna IDE
    // otwierają się i zamykają w trakcie pracy.
    private static IReadOnlyList<string> TitleHints(SessionInfo session, int hostPid, VsCodeWindow? bridgeWindow)
    {
        var hints = WindowTitleHints.For(session, IdeLocks.Read(IdeLocks.DefaultDir()), hostPid);
        var folder = bridgeWindow is null ? null : VsCodeBridge.FolderLeaf(bridgeWindow, session.Cwd);
        return folder is null ? hints : [folder, .. hints.Where(hint => !hint.Equals(folder, StringComparison.OrdinalIgnoreCase))];
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // Sesja w tle nie ma okna — otwiera się ją w nowej karcie Windows Terminal (`claude attach`),
    // a bez Windows Terminal w nowym oknie konsoli. Wyjście z niej (← albo /exit) jej nie zatrzymuje.
    private static void OpenBackgroundSession(SessionInfo session)
    {
        if (session.ShortId.Length == 0) return;
        var terminal = FindOnPath("wt.exe");
        try
        {
            if (terminal is not null)
            {
                // Średnik rozdziela polecenia wt, a cudzysłów zamknąłby argument — oba znikają z tytułu.
                var title = session.Name.Replace(";", "").Replace("\"", "");
                if (title.Length > 40) title = title[..40];
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(terminal)
                {
                    Arguments = $"-w 0 nt --title \"{title}\" claude attach {session.ShortId}",
                    UseShellExecute = true,
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("claude")
                {
                    Arguments = $"attach {session.ShortId}",
                    UseShellExecute = true,
                });
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // brak wt.exe albo claude na PATH — nic więcej nie da się zrobić bez UI błędu
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var candidate = System.IO.Path.Combine(dir.Trim('"'), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Skrót klawiszowy: sesja, która najdłużej czeka na Ciebie; gdy żadna nie czeka — najnowszy wynik.
    private async Task InvokeJumpAsync()
    {
        var waiting = _sessions.Where(s => s.Kind == SessionKinds.Waiting).OrderBy(s => s.Since).ToList();
        var target = waiting.Count > 0
            ? waiting[0]
            : _sessions.Where(s => s.Kind == SessionKinds.New).OrderByDescending(s => s.Since).FirstOrDefault();
        if (target is not null)
        {
            await ShowSessionTerminalAsync(target);
            UpdateView();
        }
    }

    // --- przejrzane po skupieniu ---------------------------------------------------------------

    private void UpdateSeenByFocus(long nowMs)
    {
        var fresh = _sessions.Where(s => s.Kind == SessionKinds.New && s.Pid != 0).ToList();
        if (fresh.Count == 0) { _dwell.Clear(); return; }

        var foreground = ForegroundNow();
        var bridgeWindows = _vsCode.ReadWindows();
        foreach (var session in fresh)
        {
            // Zapytanie WMI liczy się w tle; dopóki wynik nie jest gotowy, sesja po prostu czeka
            // do następnego tyknięcia timera zamiast blokować wątek UI co sekundę.
            if (IsLookingAt(session, foreground, bridgeWindows) is not bool looking) continue;
            if (!looking) { _dwell.Remove(session.Id); continue; }
            if (!_dwell.TryGetValue(session.Id, out var since)) _dwell[session.Id] = nowMs;
            else if (nowMs - since >= SeenAfterMs)
            {
                SetSeen(session.Id);
                _dwell.Remove(session.Id);
            }
        }
    }

    private readonly record struct ForegroundWindow(IntPtr Hwnd, int Pid, string Title);

    private static ForegroundWindow ForegroundNow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return new ForegroundWindow(hwnd, (int)NativeMethods.ProcessOf(hwnd), NativeMethods.TitleOf(hwnd));
    }

    // Czy patrzysz na sesję: jej okno jest na pierwszym planie, a w nim — gdy wiadomo — jej terminal.
    // null, dopóki nie wiadomo, gdzie jest okno sesji (zapytanie WMI liczy się w tle).
    private bool? IsLookingAt(SessionInfo session, ForegroundWindow foreground, IReadOnlyList<VsCodeWindow> bridgeWindows)
    {
        if (session.Pid == 0) return false;
        if (!_hostResolver.TryGetCached(session.Pid, out var host)) return null;
        var hostPid = host.HostPid;
        if (VsCodeBridge.OwnerOf(bridgeWindows, host.Chain) is { } bridgeWindow)
        {
            // Rozszerzenie wie, który terminal jest aktywny w oknie z fokusem — pewniejsze niż tytuł.
            return hostPid == foreground.Pid && VsCodeBridge.IsLookingAt(bridgeWindow, host.Chain);
        }
        if (host.Window != IntPtr.Zero)
        {
            // Dokładne okno terminala z konsoli sesji. Okno może mieć też inne karty (tytuł to aktywna
            // karta), więc sesja z nazwą musi być w tytule; sesja bez nazwy (nazwa = projekt) — gdy jest
            // w tym oknie jedyną sesją.
            if (foreground.Hwnd != host.Window) return false;
            if (!session.Name.Equals(session.Project, StringComparison.Ordinal))
            {
                return foreground.Title.Contains(session.Name, StringComparison.Ordinal);
            }
            return _sessions.Count(s => s.Pid != 0 && _hostResolver.TryGetCached(s.Pid, out var other) && other.Window == host.Window) == 1;
        }
        var sharing = _sessions.Count(s => s.Pid != 0 && _hostResolver.TryGetCached(s.Pid, out var other) && other.HostPid == hostPid);
        // Bez rozszerzenia rozstrzyga sama nazwa sesji w tytule: nazwa projektu czy folderu jako
        // fragment pasowałaby też do tytułów innych kart (np. „api”) i gasiła nieprzejrzane wyniki.
        return hostPid != 0 && hostPid == foreground.Pid
            && (sharing == 1 || (session.Name.Length > 0 && foreground.Title.Contains(session.Name, StringComparison.Ordinal)));
    }

    // --- dźwięki i powiadomienia -----------------------------------------------------------------

    // Dźwięk i powiadomienie, gdy sesja zaczyna czekać albo kończy z nowym wynikiem — chyba że właśnie
    // na nią patrzysz. Nieaktualne powiadomienia (sesja ruszyła dalej, wynik przejrzany) znikają.
    private void AnnounceChanges(long nowMs)
    {
        // Okno sesji szuka się zawczasu (w tle), żeby przy pierwszej prośbie o zgodę było już wiadomo,
        // czy patrzysz na jej terminal.
        foreach (var session in _sessions)
        {
            if (session.Pid != 0) _hostResolver.TryGetCached(session.Pid, out _);
        }
        var changes = _alerts.Next(_sessions, nowMs, SessionAggregator.IsUsable(_agentsSnapshot, nowMs));
        foreach (var id in changes.Cleared) _notifier.Clear(id);
        if (changes.Raised.Count == 0) return;
        var foreground = ForegroundNow();
        var bridgeWindows = _vsCode.ReadWindows();
        foreach (var alert in changes.Raised)
        {
            if (IsLookingAt(alert.Session, foreground, bridgeWindows) == true) continue;
            if (_notifier.Raise(alert)) _alerts.MarkSounded(alert, nowMs);
        }
    }

    // Kliknięcie w powiadomienie przychodzi z wątku Windows — przejście do sesji robi się na wątku okna.
    private void OnNotificationActivated(string sessionId) =>
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session is null) return;
            await Safely2("powiadomienie: przejście do sesji", () => ShowSessionTerminalAsync(session));
            UpdateView();
        });

    private void SetAlertOptions(bool sounds, bool notifications, bool save = true)
    {
        _notifier.SoundsEnabled = sounds;
        _notifier.NotificationsEnabled = notifications;
        if (!notifications) _notifier.ClearAll();
        _soundsItem.IsChecked = _traySounds.Checked = sounds;
        _notificationsItem.IsChecked = _trayNotifications.Checked = notifications;
        if (save) SaveSettings();
    }

    // --- zasobnik systemowy ---------------------------------------------------------------------

    private void UpdateTray(Dictionary<string, int> counts)
    {
        var color = counts["czeka"] > 0 ? "#FF5A4E" : counts["gotowe"] > 0 ? "#3DD68C" : counts["pracuje"] > 0 ? "#FFB224" : "#6E6E6E";
        if (color != _trayIconKey)
        {
            var icon = TrayIconFactory.Create(color);
            var previous = _trayIcon;
            _tray.Icon = icon;
            if (previous is not null) TrayIconFactory.Destroy(previous);
            _trayIcon = icon;
            _trayIconKey = color;
        }
        var parts = new List<string>();
        if (counts["czeka"] > 0) parts.Add($"czeka {counts["czeka"]}");
        if (counts["pracuje"] > 0) parts.Add($"pracuje {counts["pracuje"]}");
        if (counts["gotowe"] > 0) parts.Add($"nowe wyniki {counts["gotowe"]}");
        var text = parts.Count > 0 ? "Claude Code: " + string.Join(" · ", parts) : "Claude Code: nic nie czeka";
        if (text.Length > 63) text = text[..63];
        if (_tray.Text != text) _tray.Text = text;
    }

    // --- rozmiar, położenie, widoczność ----------------------------------------------------------

    private Border ActiveCard => _size == "mini" ? Mini : Card;

    private void SetSize(string size, bool keepRightEdge)
    {
        var right = Left + ActualWidth;
        _size = size;
        Card.Visibility = size == "mini" ? Visibility.Collapsed : Visibility.Visible;
        Mini.Visibility = size == "mini" ? Visibility.Visible : Visibility.Collapsed;
        Panel.PlacementTarget = ActiveCard;
        var label = size == "mini" ? "Widok pełny" : "Widok mini";
        _sizeItem.Header = label;
        _traySize.Text = label;
        UpdateLayout();
        if (keepRightEdge) Left = right - ActualWidth;
    }

    private void SwitchSize()
    {
        ClosePanel();
        SetSize(_size == "mini" ? "full" : "mini", true);
        SaveSettings();
    }

    private void SetDefaultPosition()
    {
        // Karta 8 px od prawej krawędzi obszaru roboczego; okno jest szersze o margines na cień.
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth + ShadowMargin - 8;
        Top = area.Top + 120;
    }

    private void InvokeSnap()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd);
        var scale = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformToDevice.M11;
        var area = screen.WorkingArea;
        double left = area.Left / scale, top = area.Top / scale, right = area.Right / scale, bottom = area.Bottom / scale;
        var cardLeft = Left + ShadowMargin;
        var cardRight = Left + ActualWidth - ShadowMargin;
        var cardTop = Top + ShadowMargin;
        var cardBottom = Top + ActualHeight - ShadowMargin;
        if (Math.Abs(right - cardRight) < SnapDistance) Left = right - 8 - ActualWidth + ShadowMargin;
        else if (Math.Abs(cardLeft - left) < SnapDistance) Left = left + 8 - ShadowMargin;
        if (Math.Abs(bottom - cardBottom) < SnapDistance) Top = bottom - 8 - ActualHeight + ShadowMargin;
        else if (Math.Abs(cardTop - top) < SnapDistance) Top = top + 8 - ShadowMargin;
    }

    private void SaveSettings()
    {
        try
        {
            WidgetConfigStore.Write(_paths.ConfigFile, new WidgetConfig
            {
                Left = Left,
                Top = Top,
                Size = _size,
                Sounds = _notifier.SoundsEnabled,
                Notifications = _notifier.NotificationsEnabled,
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WidgetLog.Write(_paths, $"zapis ustawień: {error.Message}");
        }
    }

    private void RestoreSettings()
    {
        var config = WidgetConfigStore.Read(_paths.ConfigFile);
        var size = config?.Size is "mini" or "full" ? config.Size : "mini";
        SetSize(size, false);
        SetAlertOptions(config?.Sounds ?? true, config?.Notifications ?? true, save: false);

        // Zapisana pozycja może wskazywać na odłączony monitor — wtedy wraca domyślna.
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (config?.Left is double configLeft && config.Top is double configTop &&
            configLeft >= left - ShadowMargin && configLeft + ActualWidth - ShadowMargin <= right &&
            configTop >= top - ShadowMargin && configTop + 80 <= bottom)
        {
            Left = configLeft;
            Top = configTop;
        }
        else
        {
            SetDefaultPosition();
        }
    }

    // Widżet znika, gdy ukryjesz go z menu albo gdy na jego monitorze działa coś na pełnym ekranie.
    // Zasobnik zostaje zawsze.
    private void UpdateVisibility()
    {
        var hidden = _userHidden || _fullscreenHidden;
        if (hidden && IsVisible) { ClosePanel(); Hide(); }
        else if (!hidden && !IsVisible)
        {
            // Show nie zmienia kolejności okien — widżet wróciłby pod to, co w międzyczasie weszło na wierzch.
            Show();
            NativeMethods.BringToTopmost(_hwnd);
        }
        _trayShow.Text = _userHidden ? "Pokaż widżet" : "Ukryj widżet";
    }

    private void UpdateFullscreen()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundChanged = foreground != _lastForeground;
        _lastForeground = foreground;
        var fullscreen = false;
        if (foreground != IntPtr.Zero && foreground != _hwnd &&
            NativeMethods.ClassOf(foreground) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
        {
            fullscreen = NativeMethods.IsFullscreen(foreground) && NativeMethods.MonitorOf(foreground) == NativeMethods.MonitorOf(_hwnd);
        }
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            UpdateVisibility();
        }
        // Po zmianie aktywnego okna widżet wraca nad inne okna „zawsze na wierzchu” — np. nad Pulpit
        // zdalny, który przed chwilą zajmował jego monitor. Tylko przy zmianie, żeby nie przykrywać
        // co sekundę menu czy okna, które ktoś celowo trzyma nad widżetem; własne okna (panel) pomija.
        if (foregroundChanged && IsVisible && NativeMethods.ProcessOf(foreground) != Environment.ProcessId)
        {
            NativeMethods.BringToTopmost(_hwnd);
        }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            AutostartService.SetEnabled(enabled);
        }
        catch (Exception error)
        {
            WidgetLog.Write(_paths, $"autostart: {error.Message}");
        }
        _trayAutostart.Checked = AutostartService.IsEnabled();
    }

    // --- panel --------------------------------------------------------------------------------

    private void OpenPanel()
    {
        if (Panel.IsOpen) return;
        _panelSignature = "";
        Panel.IsOpen = true;
        Safely("panel", UpdateView);
        Safely("statusline", UpdateStatusLineHint);
        _hoverTimer.Start();
    }

    // Bez danych ze statusline (cudza, której instalator nie mógł bezpiecznie podpiąć) nie ma limitów
    // ani kontekstu — panel mówi dlaczego i jak to naprawić, zamiast pokazywać puste paski.
    private void UpdateStatusLineHint()
    {
        var kind = ClaudeSettings.Inspect(ClaudeSettings.DefaultPath);
        StatusLineHint.Visibility = kind is StatusLineKind.Foreign or StatusLineKind.Missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClosePanel()
    {
        _pinned = false;
        PanelBody.BorderBrush = Brushes.Brush("#14FFFFFF");
        Panel.IsOpen = false;
        _hoverTimer.Stop();
    }

    // --- menu ---------------------------------------------------------------------------------

    private void BuildMenus()
    {
        var menu = new ContextMenu();
        _sizeItem = new MenuItem();
        _sizeItem.Click += (_, _) => SwitchSize();
        var dockItem = new MenuItem { Header = "Przyklej do prawej krawędzi" };
        dockItem.Click += (_, _) => { SetDefaultPosition(); SaveSettings(); };
        var hideItem = new MenuItem { Header = "Ukryj (przywrócisz z zasobnika)" };
        hideItem.Click += (_, _) => { _userHidden = true; UpdateVisibility(); };
        var closeItem = new MenuItem { Header = "Zamknij widżet" };
        closeItem.Click += (_, _) => Close();
        // IsCheckable przełącza znaczek sam, zanim przyjdzie Click — stąd odczyt IsChecked.
        _soundsItem = new MenuItem { Header = "Dźwięki", IsCheckable = true };
        _soundsItem.Click += (_, _) => Safely("dźwięki", () => SetAlertOptions(_soundsItem.IsChecked, _notifier.NotificationsEnabled));
        _notificationsItem = new MenuItem { Header = "Powiadomienia Windows", IsCheckable = true };
        _notificationsItem.Click += (_, _) => Safely("powiadomienia", () => SetAlertOptions(_notifier.SoundsEnabled, _notificationsItem.IsChecked));
        menu.Items.Add(_sizeItem);
        menu.Items.Add(_soundsItem);
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(new Separator());
        foreach (var item in new[] { dockItem, hideItem, closeItem }) menu.Items.Add(item);
        foreach (var surface in new[] { Card, Mini }) surface.ContextMenu = menu;

        _tray = new NotifyIcon();
        var trayMenu = new ContextMenuStrip();
        _trayShow = (ToolStripMenuItem)trayMenu.Items.Add("Ukryj widżet");
        _trayShow.Click += (_, _) => Safely("zasobnik: pokaż/ukryj", () => { _userHidden = !_userHidden; UpdateVisibility(); });
        _traySize = (ToolStripMenuItem)trayMenu.Items.Add("Widok pełny");
        _traySize.Click += (_, _) => Safely("zasobnik: rozmiar", () => { if (_userHidden) { _userHidden = false; UpdateVisibility(); } SwitchSize(); });
        var trayDock = (ToolStripMenuItem)trayMenu.Items.Add("Przyklej do prawej krawędzi");
        trayDock.Click += (_, _) => Safely("zasobnik: dokowanie", () => { if (_userHidden) { _userHidden = false; UpdateVisibility(); } SetDefaultPosition(); SaveSettings(); });
        _trayAutostart = new ToolStripMenuItem("Uruchamiaj przy logowaniu") { Checked = AutostartService.IsEnabled() };
        _trayAutostart.Click += (_, _) => Safely("zasobnik: autostart", () => SetAutostart(!_trayAutostart.Checked));
        trayMenu.Items.Add(_trayAutostart);
        _traySounds = new ToolStripMenuItem("Dźwięki");
        _traySounds.Click += (_, _) => Safely("zasobnik: dźwięki", () => SetAlertOptions(!_notifier.SoundsEnabled, _notifier.NotificationsEnabled));
        trayMenu.Items.Add(_traySounds);
        _trayNotifications = new ToolStripMenuItem("Powiadomienia Windows");
        _trayNotifications.Click += (_, _) => Safely("zasobnik: powiadomienia", () => SetAlertOptions(_notifier.SoundsEnabled, !_notifier.NotificationsEnabled));
        trayMenu.Items.Add(_trayNotifications);
        trayMenu.Items.Add(new ToolStripSeparator());
        _trayUpdateItem = new ToolStripMenuItem("Zaktualizuj i uruchom ponownie") { Visible = false };
        _trayUpdateItem.Click += (_, _) => Safely("aktualizacja", () =>
        {
            // Bez tego ikona zostaje widoczna, aż stary proces zniknie — po restarcie wygląda jak duch.
            _tray.Visible = false;
            _tray.Dispose();
            if (_trayIcon is not null) { TrayIconFactory.Destroy(_trayIcon); _trayIcon = null; }
            _notifier.ClearAll(); // OnClosed nie przyjdzie — proces kończy się od razu
            _updates.ApplyAndRestart(_restartArgs);
        });
        trayMenu.Items.Add(_trayUpdateItem);
        var trayClose = (ToolStripMenuItem)trayMenu.Items.Add("Zamknij widżet");
        trayClose.Click += (_, _) => Safely("zasobnik: zamknij", () => System.Windows.Application.Current.Dispatcher.Invoke(Close));
        _tray.ContextMenuStrip = trayMenu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Safely("zasobnik: klik", () => System.Windows.Application.Current.Dispatcher.Invoke(() => { _userHidden = !_userHidden; UpdateVisibility(); }));
            }
        };
        _updates.UpdateReady += () => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _trayUpdateItem.Text = $"Zaktualizuj do {_updates.PendingVersion} i uruchom ponownie";
            _trayUpdateItem.Visible = true;
        });
    }

    // --- zdarzenia okna -------------------------------------------------------------------------

    private void WireEvents()
    {
        foreach (var surface in new[] { Card, Mini })
        {
            surface.MouseEnter += (_, _) => { _openTimer.Stop(); _openTimer.Start(); };
            surface.MouseLeave += (_, _) => _openTimer.Stop();
            // Przeciągnięcie przesuwa widżet; kliknięcie w miejscu przypina albo zamyka panel.
            // DragMove rzuca, gdy przycisk myszy puszczono tuż przed wywołaniem (WPF nie zdążył
            // złapać MouseDown) — Safely zamiast wywalać widżet na pustym miejscu na pulpicie.
            surface.MouseLeftButtonDown += (_, _) => Safely("przeciąganie", () =>
            {
                _openTimer.Stop();
                var startLeft = Left;
                var startTop = Top;
                DragMove();
                if (Math.Abs(Left - startLeft) >= 1 || Math.Abs(Top - startTop) >= 1)
                {
                    ClosePanel();
                    InvokeSnap();
                    SaveSettings();
                }
                else if (_pinned)
                {
                    ClosePanel();
                }
                else
                {
                    OpenPanel();
                    _pinned = true;
                    PanelBody.BorderBrush = Brushes.Brush("#40FFFFFF");
                }
            });
        }

        // Przyciski obsługują wciśnięcie myszy same, więc karta nie zaczyna przy nich przeciągania.
        StatusLineHelp.RequestNavigate += (_, e) => Safely("pomoc statusline", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }));
        MinimizeButton.Click += (_, _) => Safely("zmniejszanie", SwitchSize);
        ExpandButton.Click += (_, _) => Safely("rozwijanie", SwitchSize);

        _openTimer.Tick += (_, _) =>
        {
            _openTimer.Stop();
            if (ActiveCard.IsMouseOver) OpenPanel();
        };
        _hoverTimer.Tick += (_, _) =>
        {
            if (!_pinned && !ActiveCard.IsMouseOver && !PanelBody.IsMouseOver) ClosePanel();
        };

        _refreshTimer.Tick += (_, _) =>
        {
            Safely("odświeżanie", UpdateView);
            Safely("pełny ekran", UpdateFullscreen);
            Safely("przejrzenie", () => UpdateSeenByFocus(NowMs()));
        };
        _agentsTimer.Tick += async (_, _) => await Safely2("lista sesji", RunAgentsTickAsync);
        _cleanupTimer.Tick += (_, _) => Safely("sprzątanie", () => _aggregator.CleanupOrphans());
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); Safely("obserwacja", UpdateView); };
    }

    private void Safely(string what, Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            // Ten sam błąd co sekundę zalałby log; zapisuje się tylko zmiana.
            var message = $"{what}: {error.Message}";
            if (message != _lastError)
            {
                _lastError = message;
                WidgetLog.Write(_paths, message);
            }
        }
    }

    private async Task Safely2(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception error)
        {
            var message = $"{what}: {error.Message}";
            if (message != _lastError)
            {
                _lastError = message;
                WidgetLog.Write(_paths, message);
            }
        }
    }

    // agents.mjs odświeżał listę z `claude agents --json` w osobnym procesie node; tu to samo
    // wywołanie działa asynchronicznie w procesie widżetu, więc okno nigdy na nie nie czeka.
    // Co 4 s, gdy coś pracuje albo czeka w tle lub panel jest otwarty; poza tym co 20 s.
    private async Task RunAgentsTickAsync()
    {
        if (_agentsRunning) return;
        var active = Panel.IsOpen || _sessions.Any(s => s.IsBackground && s.Kind is SessionKinds.Waiting or SessionKinds.Working);
        if (!active && (DateTime.UtcNow - _agentsStartedAt).TotalSeconds < AgentsIdleSeconds) return;

        _agentsRunning = true;
        _agentsStartedAt = DateTime.UtcNow;
        try
        {
            var now = NowMs();
            var previous = _agentsPrevious;
            var path = Environment.GetEnvironmentVariable("PATH");
            var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            // FindClaude (pętla File.Exists po PATH) i Process.Start są synchroniczne — Task.Run,
            // żeby nawet ten pierwszy kawałek nie ruszał się na wątku UI.
            var snapshot = await Task.Run(() => AgentsRunner.RunAsync(previous, now, File.Exists, path, pathExt, CancellationToken.None));
            _agentsSnapshot = snapshot;
            _agentsPrevious = snapshot.Sessions;
        }
        finally
        {
            _agentsRunning = false;
        }
    }

    // Co 200 ms w wersji PowerShell obserwowało znacznik czasu katalogu; tu FileSystemWatcher robi
    // to samo zdarzeniowo. Debounce, bo hook i statusline potrafią zapisać kilka plików naraz.
    private void StartWatcher()
    {
        if (!Directory.Exists(_paths.StateDir)) Directory.CreateDirectory(_paths.StateDir);
        _watcher = new FileSystemWatcher(_paths.StateDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        void OnChange(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() => { _debounceTimer.Stop(); _debounceTimer.Start(); });
        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += (s, e) => OnChange(s, e);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        RestoreSettings();
        var hint = "Kliknij sesję, aby przejść do jej terminala. Kliknij sygnalizator, aby przypiąć panel.";
        try
        {
            var source = HwndSource.FromHwnd(_hwnd)!;
            _hotkey = new GlobalHotkey(source, HotkeyModifiers, HotkeyKeyCode);
            if (_hotkey.Registered)
            {
                _hotkey.Pressed += async () => await Safely2("skrót", InvokeJumpAsync);
                hint = $"Kliknij sesję, aby przejść do jej terminala; {HotkeyLabel} przenosi do tej, która czeka najdłużej. Kliknij sygnalizator, aby przypiąć panel.";
            }
            else
            {
                WidgetLog.Write(_paths, $"skrót {HotkeyLabel} jest zajęty przez inny program");
            }
        }
        catch (Exception error)
        {
            WidgetLog.Write(_paths, $"skrót: {error.Message}");
        }
        PanelHint.Text = hint;

        _tray.Visible = true;
        Safely("start", UpdateView);
        Safely("sprzątanie", () => _aggregator.CleanupOrphans());
        Safely("obserwacja katalogu", StartWatcher);
        _refreshTimer.Start();
        _agentsTimer.Start();
        _cleanupTimer.Start();
        _ = _updates.StartAsync(message => WidgetLog.Write(_paths, message), CancellationToken.None);
        _ = Task.Run(() => Safely("rozszerzenie VS Code", () => VsCodeExtensionInstaller.InstallIfNeeded(_paths, message => WidgetLog.Write(_paths, message))));
        // Tylko zainstalowany widżet: uruchomienie deweloperskie wpisałoby swoją ścieżkę do settings.json.
        if (_updates.IsInstalled) _ = Task.Run(InstallHooks.ReconcileSettings);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _agentsTimer.Stop();
        _cleanupTimer.Stop();
        _watcher?.Dispose();
        _hotkey?.Dispose();
        _notifier.ClearAll();
        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIcon is not null) TrayIconFactory.Destroy(_trayIcon);
    }
}
