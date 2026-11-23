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
public partial class MainWindow : Window, ISettingsHost
{
    // Rozmiary z widget.ps1 (patrz komentarze przy zmiennych $CardInner itd.).
    private const double CardInner = 122;
    private const double PanelInner = 306;
    private const double ShadowMargin = 24;
    private const double SnapDistance = 24;
    private const long SeenAfterMs = 3000;
    /// <summary>Szerokość części wyspy ze stanem i limitami (patrz MainWindow.xaml).</summary>
    public const double IslandExtraWidth = 394;
    private const double PanelMargin = 16;
    private const string PanelHintBase = "Kliknij sesję, aby przejść do jej terminala. Kliknij sygnalizator, aby przypiąć panel.";
    private const int AgentsIdleSeconds = 20;
    private const string IdleBorder = "#47FFFFFF";
    private const string CheckUpdatesLabel = "Sprawdź aktualizacje";
    private static readonly TimeSpan AgentsActiveInterval = TimeSpan.FromSeconds(4);

    private static readonly (string Key, string On, string Off)[] LightPalette =
    [
        ("czeka", "#FF5A4E", "#3D1D1A"),
        ("pracuje", "#FFB224", "#3B2E15"),
        ("gotowe", "#3DD68C", "#173324"),
    ];

    private static readonly (string Key, string Label)[] Views = [("full", "Pełny"), ("mini", "Mini"), ("pet", "Pet")];

    private readonly WidgetPaths _paths;
    private readonly SessionAggregator _aggregator;
    private readonly HostResolver _hostResolver = new();
    private readonly VsCodeBridge _vsCode;
    private OrcaCli? _orcaCli;
    private OrcaNavigator? _orca;
    private CancellationTokenSource? _orcaShow;
    private IReadOnlyDictionary<string, OrcaLabel> _orcaLabels = new Dictionary<string, OrcaLabel>();
    private DateTime _orcaLabelsAt;
    private string _orcaLabelsFor = "";
    private bool _orcaLabelsRunning;
    private readonly AlertPlanner _alerts = new();
    private readonly SessionNotifier _notifier;
    private MenuItem _soundsItem = null!, _notificationsItem = null!, _muteItem = null!, _unmuteItem = null!;
    private ToolStripMenuItem _traySounds = null!, _trayNotifications = null!, _trayMute = null!, _trayUnmute = null!;
    private WidgetConfig _config = new();
    private SettingsWindow? _settingsWindow;
    private string? _registeredHotkey;
    private string _hotkeyProblem = "";
    private readonly UpdateService _updates = new();
    private LimitsPoller? _limitsPoller;

    /// <summary>Widżet przedstawia się serwerowi własną nazwą i wersją, a nie jako Claude Code.</summary>
    private static readonly string UserAgent = $"claude-widget/{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
    private readonly Dictionary<string, RadialGradientBrush> _litFill = [];
    private readonly Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> _fullGlow = [];
    private readonly Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> _miniGlow = [];
    private readonly Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> _islandGlow = [];
    private readonly List<(Dictionary<string, Ellipse> Ellipses, Dictionary<string, TextBlock> Counts, Dictionary<string, System.Windows.Media.Effects.DropShadowEffect> Glow)> _lightSets = [];
    private readonly Dictionary<string, long> _dwell = [];

    private MeterControl _mContext = null!;
    private LimitMeterList _cardLimits = null!, _panelLimits = null!, _islandLimits = null!;
    private ServerLimits? _lastServerLimits;
    private Style _rowStyle = null!, _waitingRowStyle = null!;
    private readonly PetControl _pet = new();
    // Pierwsza, pikselowa wersja Peta — wybierana jako strój „pixel”; tworzona dopiero wtedy.
    private PixelPetControl? _pixelPet;
    private IReadOnlyDictionary<string, int> _petCounts = new Dictionary<string, int>();
    private readonly Dictionary<string, MenuItem> _viewItems = [];
    private readonly List<(string Key, MenuItem Item, ToolStripMenuItem TrayItem)> _skinItems = [];
    private readonly Dictionary<string, ToolStripMenuItem> _trayViewItems = [];
    private ToolStripMenuItem _trayShow = null!, _trayAutostart = null!, _trayUpdateItem = null!, _trayCheckItem = null!;
    private NotifyIcon _tray = null!;
    private Icon? _trayIcon;
    private string _trayIconKey = "";
    private GlobalHotkey? _hotkey;
    private FileSystemWatcher? _watcher, _configWatcher;
    private readonly DispatcherTimer _configDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
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
    private DockEdge _dock;
    // Środek wyspy wzdłuż krawędzi; null — środek obszaru roboczego.
    private double? _anchor;
    private bool _islandExpanded;
    private readonly List<(DockEdge Edge, MenuItem Item, ToolStripMenuItem TrayItem)> _dockItems = [];
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
        Pet.Child = _pet.Root;
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
            _islandGlow[key] = Brushes.Glow(color, 16, 0.9);
        }

        _lightSets.Add((
            new() { ["czeka"] = LightCzeka, ["pracuje"] = LightPracuje, ["gotowe"] = LightGotowe },
            new() { ["czeka"] = CountCzeka, ["pracuje"] = CountPracuje, ["gotowe"] = CountGotowe },
            _fullGlow));
        _lightSets.Add((
            new() { ["czeka"] = MiniCzeka, ["pracuje"] = MiniPracuje, ["gotowe"] = MiniGotowe },
            new() { ["czeka"] = MiniCountCzeka, ["pracuje"] = MiniCountPracuje, ["gotowe"] = MiniCountGotowe },
            _miniGlow));
        _lightSets.Add((
            new() { ["czeka"] = IslandCzeka, ["pracuje"] = IslandPracuje, ["gotowe"] = IslandGotowe },
            new() { ["czeka"] = IslandCountCzeka, ["pracuje"] = IslandCountPracuje, ["gotowe"] = IslandCountGotowe },
            _islandGlow));
    }

    private void BuildMeters()
    {
        // Kontekst należy do sesji, nie do limitów konta — stoi przy niej i nie podlega trybowi „zostało”.
        _mContext = new MeterControl("Kontekst sesji", CardInner, 11, "#A6A6A6", 0);
        ContextMeter.Children.Add(_mContext.Root);

        _cardLimits = new LimitMeterList(Meters, row => row.CardLabel, label => new MeterControl(label, CardInner, 11, "#A6A6A6", 10));
        _panelLimits = new LimitMeterList(PanelLimits, row => row.PanelLabel, label => new MeterControl(label, PanelInner, 12, "#C8C8C8", 10));
        _islandLimits = new LimitMeterList(IslandMeters, row => row.IslandLabel, label => new MeterControl(label, 80));
    }

    // --- odświeżanie widoku -------------------------------------------------------------------

    private long NowMs() => DateTimeOffset.Now.ToUnixTimeMilliseconds();

    private void UpdateView()
    {
        var nowMs = NowMs();
        _sessions = _aggregator.GetSessions(nowMs, _agentsSnapshot, _orcaLabels);
        AnnounceChanges(nowMs);
        var limits = JsonStore.Read(_paths.LimitsFile, StateJson.Default.AccountLimits);
        // Plik chwilowo zajęty (podmiana w trakcie) nie może na sekundę zmienić zestawu pasków.
        var serverLimits = JsonStore.Read(_paths.ServerLimitsFile, StateJson.Default.ServerLimits)
            ?? (File.Exists(_paths.ServerLimitsFile) ? _lastServerLimits : null);
        _lastServerLimits = serverLimits;
        long? measuredAt = limits is { UpdatedAt: > 0 } ? limits.UpdatedAt : null;
        var limitRows = LimitRows.Build(limits, serverLimits, nowMs, panel: false);
        var limitMode = _config.LimitMode;
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
        _petCounts = counts;
        _pet.Set(counts);
        _pixelPet?.Set(counts);

        if (focus is not null)
        {
            var kindInfo = SessionKindCatalog.Kinds[focus.Kind];
            StatusLabel.Text = kindInfo.Label;
            StatusLabel.Foreground = Brushes.Brush(focus.Kind == SessionKinds.Idle ? "#8A8A8A" : kindInfo.Color);
            StatusDetail.Text = focus.Kind == SessionKinds.Idle ? "wyniki przejrzane" : SessionFormatting.GetDetail(focus, nowMs);
            SessionLine.Text = $"{focus.Project} · {Formatting.FormatSessions(_sessions.Count)}";
            IslandStatus.Text = $"{kindInfo.Label} · {focus.Project}";
            IslandStatus.Foreground = StatusLabel.Foreground;
            IslandDetail.Text = StatusDetail.Text;
        }
        else
        {
            StatusLabel.Text = "Brak sesji";
            StatusLabel.Foreground = Brushes.Brush("#8A8A8A");
            StatusDetail.Text = "uruchom Claude Code";
            SessionLine.Text = "";
            IslandStatus.Text = StatusLabel.Text;
            IslandStatus.Foreground = StatusLabel.Foreground;
            IslandDetail.Text = StatusDetail.Text;
        }

        _cardLimits.Update(limitRows, limitMode);
        // Wyspa ma miejsce na dwa wiersze: limity konta mają pierwszeństwo przed limitami modeli.
        _islandLimits.Update(limitRows.Take(2), limitMode);
        CardLimitMode.Text = LimitDisplay.Word(limitMode);
        var contextPct = focus?.ContextPct;
        _mContext.Set(contextPct, SessionFormatting.GetContextNote(focus), contextPct >= 80 ? "#FFB224" : "#BDBDBD");
        Freshness.Text = Formatting.GetFreshnessText(measuredAt, limits?.Source);

        // Obramowanie ostrzega przed końcem limitu także w widoku mini, bez najeżdżania.
        var warn = LimitRows.Warning(limitRows);
        // Bez ostrzeżenia ramka zostaje wyraźna: przy zgaszonych światłach ciemna karta ginęła na ciemnej tapecie.
        var borderBrush = Brushes.Brush(warn is not null ? "#B3" + warn[1..] : IdleBorder);
        Card.BorderBrush = borderBrush;
        Mini.BorderBrush = borderBrush;
        Island.BorderBrush = borderBrush;
        Mini.BorderThickness = new Thickness(warn is not null ? 1.5 : 1);

        UpdateTray(counts);

        if (Panel.IsOpen)
        {
            UpdatePanel(_sessions, LimitRows.Build(limits, serverLimits, nowMs, panel: true), measuredAt, limits?.Source, nowMs);
        }
    }

    private void UpdatePanel(IReadOnlyList<SessionInfo> sessions, IReadOnlyList<LimitRow> limitRows, long? measuredAt, string? source, long nowMs)
    {
        PanelCount.Text = sessions.Count.ToString();
        PanelFreshness.Text = Formatting.GetFreshnessText(measuredAt, source);
        LimitModeText.Text = $"{LimitDisplay.Word(_config.LimitMode)} ⇅";

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
        _panelLimits.Update(limitRows, _config.LimitMode);
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
        foreach (var light in new[] { LightCzeka, MiniCzeka, IslandCzeka })
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
        // Wygrywa ostatni klik: przełączanie Orca po wcześniejszym kliknięciu, jeśli jeszcze trwa,
        // nie może przestawić Orca, gdy idziesz już gdzie indziej.
        _orcaShow?.Cancel();
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
        // Sesja w Orca: okno Orca jest już z przodu, a Orca przełącza się na terminal albo workspace
        // sesji. Rozstrzyga proces Orca jako host, nie zmienne Orca — te dziedziczy też np. VS Code
        // otwarte z terminala Orca. Sesja bez miejsca z hooka (np. z listy `claude agents`, zanim
        // hook cokolwiek zapisał) trafia do Orca po samym katalogu. Przełączanie idzie w tle:
        // kilka wywołań orca.exe trwa nawet parę sekund, a panel ma się zamknąć od razu.
        if (IsOrcaProcess(host.HostPid)) _ = ShowInOrcaAsync(session, session.Orca ?? new OrcaPlace(null, null));
    }

    private async Task ShowInOrcaAsync(SessionInfo session, OrcaPlace place)
    {
        _orca ??= FindOrcaCli() is { } cli ? new OrcaNavigator(cli) : null;
        if (_orca is null)
        {
            WidgetLog.Write(_paths, "Orca: nie znaleziono orca.exe — zostaje samo okno Orca");
            return;
        }
        using var show = new CancellationTokenSource();
        _orcaShow = show;
        try
        {
            var result = await _orca.ShowAsync(place, session.Cwd, show.Token);
            if (!show.IsCancellationRequested && (result.Errors.Count > 0 || result.Shown == OrcaShown.NotFound))
            {
                WidgetLog.Write(_paths, $"Orca: sesja {session.Id} — {result.Shown} ({string.Join("; ", result.Errors)})");
            }
        }
        catch (Exception error)
        {
            // Zadanie w tle nie ma kto obsłużyć — błąd idzie do logu, a widżet działa dalej.
            WidgetLog.Write(_paths, $"Orca: {error.Message}");
        }
        finally
        {
            if (ReferenceEquals(_orcaShow, show)) _orcaShow = null;
        }
    }

    private OrcaCli? FindOrcaCli() =>
        _orcaCli ??= OrcaCli.Find(File.Exists, Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("PATH"));

    // Nazwy sesji z Orca: co 15 s, a od razu, gdy pojawi się nowa sesja z Orca. Bez żadnej sesji
    // z Orca widżet w ogóle nie uruchamia orca.exe.
    private async Task RefreshOrcaLabelsAsync()
    {
        if (_orcaLabelsRunning) return;
        var ids = string.Join(",", _sessions.Select(s => s.Orca?.AgentSessionId).OfType<string>().Order(StringComparer.Ordinal));
        if (ids.Length == 0)
        {
            _orcaLabelsFor = "";
            return;
        }
        if (ids == _orcaLabelsFor && DateTime.UtcNow - _orcaLabelsAt < TimeSpan.FromSeconds(15)) return;
        if (FindOrcaCli() is not { } cli) return;

        _orcaLabelsRunning = true;
        _orcaLabelsAt = DateTime.UtcNow;
        _orcaLabelsFor = ids;
        try
        {
            if (await Task.Run(() => OrcaLabel.ListAsync(cli, CancellationToken.None)) is { } labels)
            {
                _orcaLabels = labels;
                UpdateView();
            }
        }
        finally
        {
            _orcaLabelsRunning = false;
        }
    }

    private static bool IsOrcaProcess(int pid)
    {
        if (pid == 0) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName.Equals("Orca", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
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
            if (_notifier.Raise(alert, nowMs)) _alerts.MarkSounded(alert, nowMs);
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

    // --- ustawienia ------------------------------------------------------------------------------

    // Jedyne miejsce, które zmienia ustawienia: menu, okno ustawień, plik i koniec wyciszenia.
    private void ApplyConfig(WidgetConfig config, bool save = true)
    {
        _config = config;
        _pet.SetSkin(config.PetSkinKey);
        ShowPetVersion(config.PetSkinKey == "pixel");
        foreach (var (key, item, trayItem) in _skinItems)
            item.IsChecked = trayItem.Checked = key == config.PetSkinKey;
        _notifier.Configure(config);
        _alerts.SoundThrottleMs = config.SoundRepeatMs;
        if (config.IsMuted(NowMs())) _notifier.ClearAll();
        _soundsItem.IsChecked = _traySounds.Checked = config.SoundsOn;
        _notificationsItem.IsChecked = _trayNotifications.Checked = config.NotificationsOn;
        UpdateMuteMenu();
        UpdateOpacity();
        if (_hwnd != IntPtr.Zero && config.HotkeyText != _registeredHotkey) RegisterHotkey();
        if (!config.HidesOnFullscreen && _fullscreenHidden)
        {
            _fullscreenHidden = false;
            UpdateVisibility();
        }
        if (save) SaveSettings();
    }

    // Zmiana spoza okna ustawień — otwarte okno pokazuje ją od razu.
    private void ChangeConfig(Func<WidgetConfig, WidgetConfig> change)
    {
        ApplyConfig(change(_config));
        _settingsWindow?.Refresh();
    }

    private void Mute(MuteDuration duration) =>
        ChangeConfig(c => c with { MutedUntil = MuteOptions.Until(duration, DateTimeOffset.Now).ToUnixTimeMilliseconds() });

    private void UpdateMuteMenu()
    {
        var now = DateTimeOffset.Now;
        var muted = _config.IsMuted(now.ToUnixTimeMilliseconds());
        var header = muted
            ? MuteOptions.Describe(DateTimeOffset.FromUnixTimeMilliseconds(_config.MutedUntil!.Value).ToLocalTime(), now)
            : "Wycisz";
        _muteItem.Header = header;
        _trayMute.Text = header;
        _unmuteItem.IsEnabled = _trayUnmute.Enabled = muted;
    }

    // Wyciszenie kończy się samo — wtedy menu i okno ustawień wracają do „Wycisz”.
    private void ExpireMute(long nowMs)
    {
        if (_config.MutedUntil is long until && until <= nowMs) ChangeConfig(c => c with { MutedUntil = null });
    }

    // Pod kursorem i z otwartym panelem karta jest zawsze w pełni widoczna.
    private void UpdateOpacity() => Opacity = IsMouseOver || Panel.IsOpen ? 1 : _config.OpacityPercent / 100.0;

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        var text = _config.HotkeyText;
        _registeredHotkey = text;
        _hotkeyProblem = "";
        PanelHint.Text = PanelHintBase;
        if (text.Length == 0)
        {
            _hotkeyProblem = "Skrót jest wyłączony.";
            return;
        }
        if (!Hotkey.TryParse(text, out var hotkey))
        {
            _hotkeyProblem = $"Nieprawidłowy skrót „{text}” w pliku ustawień.";
            WidgetLog.Write(_paths, $"skrót: nieprawidłowy „{text}”");
            return;
        }
        var label = hotkey.ToString();
        try
        {
            _hotkey = new GlobalHotkey(HwndSource.FromHwnd(_hwnd)!, hotkey.Modifiers, hotkey.KeyCode);
            if (_hotkey.Registered)
            {
                _hotkey.Pressed += async () => await Safely2("skrót", InvokeJumpAsync);
                PanelHint.Text = $"Kliknij sesję, aby przejść do jej terminala; {label} przenosi do tej, która czeka najdłużej. Kliknij sygnalizator, aby przypiąć panel.";
            }
            else
            {
                _hotkeyProblem = $"{label} jest zajęty przez inny program — wybierz inny skrót.";
                WidgetLog.Write(_paths, $"skrót {label} jest zajęty przez inny program");
            }
        }
        catch (Exception error)
        {
            _hotkeyProblem = $"Nie udało się ustawić skrótu: {error.Message}";
            WidgetLog.Write(_paths, $"skrót: {error.Message}");
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // Ręczne zmiany w widget-config.json działają od razu. Pozycja i rozmiar z pliku się nie liczą —
    // tym rządzi przeciąganie; własny zapis widżetu nic nie zmienia, więc niczego nie uruchamia.
    private void StartConfigWatcher()
    {
        _configWatcher = new FileSystemWatcher(_paths.WidgetDir, System.IO.Path.GetFileName(_paths.ConfigFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        void OnChange(object sender, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() => { _configDebounce.Stop(); _configDebounce.Start(); });
        _configWatcher.Changed += OnChange;
        _configWatcher.Created += OnChange;
        _configWatcher.Renamed += (s, e) => OnChange(s, e);
    }

    private void ReloadConfig()
    {
        var loaded = WidgetConfigStore.Read(_paths.ConfigFile);
        if (loaded is null)
        {
            // W trakcie zapisu albo z błędem w JSON-ie — zostają bieżące ustawienia.
            if (File.Exists(_paths.ConfigFile)) WidgetLog.Write(_paths, "ustawienia: nie da się odczytać widget-config.json");
            return;
        }
        if (loaded.WithoutPlacement() == _config.WithoutPlacement()) return;
        ApplyConfig(loaded with
        {
            Left = _config.Left,
            Top = _config.Top,
            Size = _config.Size,
            Dock = _config.Dock,
            DockAnchor = _config.DockAnchor,
        }, save: false);
        _settingsWindow?.Refresh();
    }

    private void OpenConfigFile()
    {
        if (!File.Exists(_paths.ConfigFile)) SaveSettings();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_paths.ConfigFile) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // .json bez skojarzonego programu
            System.Diagnostics.Process.Start("notepad.exe", $"\"{_paths.ConfigFile}\"");
        }
    }

    WidgetConfig ISettingsHost.Config => _config;

    string ISettingsHost.WidgetDir => _paths.WidgetDir;

    string ISettingsHost.HotkeyProblem => _hotkeyProblem;

    void ISettingsHost.UpdateConfig(Func<WidgetConfig, WidgetConfig> change) => Safely("ustawienia", () => ApplyConfig(change(_config)));

    void ISettingsHost.PreviewSound(AlertKind kind, string? choice) =>
        Safely("odsłuch dźwięku", () => _notifier.Preview(kind, choice, _config.VolumePercent));

    void ISettingsHost.OpenConfigFile() => Safely("plik ustawień", OpenConfigFile);

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

    // Pet stoi przy każdej krawędzi; wyspa zastępuje tylko kartę i mini przy górnej albo dolnej.
    private bool ShowsIsland => Docking.IsHorizontal(_dock) && _size != "pet";

    private Border ActiveCard => _size == "pet" ? Pet : ShowsIsland ? Island : _size == "mini" ? Mini : Card;

    private void SetSize(string size, bool keepRightEdge, bool animate = false)
    {
        var right = Left + ActualWidth;
        _size = size;
        foreach (var (key, item) in _viewItems) item.IsChecked = key == size;
        foreach (var (key, item) in _trayViewItems) item.Checked = key == size;
        ApplyLayout(animate);
        // Swobodna karta rośnie w lewo, jak dotąd; przyklejoną ustawia ApplyLayout.
        if (keepRightEdge && _dock == DockEdge.None) Left = right - ActualWidth;
    }

    private void SwitchSize(string size)
    {
        ClosePanel(collapseIsland: false);
        SetSize(size, true, animate: true);
        SaveSettings();
    }

    // Który widok widać (karta, mini, wyspa, pet), jak wygląda wyspa przy danej krawędzi i gdzie stoi okno.
    private void ApplyLayout(bool animate = false)
    {
        var island = ShowsIsland;
        Card.Visibility = !island && _size == "full" ? Visibility.Visible : Visibility.Collapsed;
        Mini.Visibility = !island && _size == "mini" ? Visibility.Visible : Visibility.Collapsed;
        Pet.Visibility = _size == "pet" ? Visibility.Visible : Visibility.Collapsed;
        Island.Visibility = island ? Visibility.Visible : Visibility.Collapsed;
        if (island)
        {
            // Wyspa „wyrasta” z krawędzi: płaska i bez obramowania od jej strony, cień w stronę ekranu.
            var top = _dock == DockEdge.Top;
            Island.CornerRadius = top ? new CornerRadius(0, 0, 16, 16) : new CornerRadius(16, 16, 0, 0);
            Island.BorderThickness = top ? new Thickness(1, 0, 1, 1) : new Thickness(1, 1, 1, 0);
            IslandShadow.Direction = top ? 270 : 90;
            var full = _size == "full";
            IslandSizeButton.Content = full ? (top ? "\uE70E" : "\uE70D") : "\uE718";
            IslandSizeButton.ToolTip = full ? "Zwijaj wyspę, gdy nie najeżdżasz" : "Zostaw wyspę rozwiniętą";
            SetIslandExpanded(full || Panel.IsOpen, animate);
        }
        Panel.PlacementTarget = ActiveCard;
        foreach (var (edge, item, trayItem) in _dockItems) item.IsChecked = trayItem.Checked = edge == _dock;
        UpdateLayout();
        PlaceDocked();
    }

    // Wyspa rozwija się tylko w szerz; okno trzyma przy tym jej środek (PlaceDocked po zmianie rozmiaru).
    private void SetIslandExpanded(bool expand, bool animate)
    {
        var width = expand ? IslandExtraWidth : 0;
        if (!animate)
        {
            _islandExpanded = expand;
            IslandExtra.BeginAnimation(WidthProperty, null);
            IslandExtra.BeginAnimation(OpacityProperty, null);
            IslandExtra.Width = width;
            IslandExtra.Opacity = expand ? 1 : 0;
            return;
        }
        if (_islandExpanded == expand) return;
        _islandExpanded = expand;
        var duration = new Duration(TimeSpan.FromMilliseconds(expand ? 200 : 150));
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        IslandExtra.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(width, duration) { EasingFunction = ease });
        IslandExtra.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(expand ? 1 : 0, duration));
    }

    // Obszar roboczy monitora, na którym stoi widżet, w jednostkach WPF.
    private Box WorkArea()
    {
        var area = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea;
        var scale = DeviceScale();
        return new Box(area.Left / scale, area.Top / scale, area.Width / scale, area.Height / scale);
    }

    private double DeviceScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    // Widoczna karta bez marginesu na cień.
    private Box CardBox() => new(Left + ShadowMargin, Top + ShadowMargin, ActualWidth - 2 * ShadowMargin, ActualHeight - 2 * ShadowMargin);

    private void PlaceDocked()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_dock != DockEdge.None)
        {
            var card = CardBox();
            (Left, Top) = PlaceWidget(card, WorkArea());
        }
        UpdatePetLook();
    }

    private (double Left, double Top) PlaceWidget(Box card, Box area)
    {
        var position = Docking.Place(_dock, card.Width, card.Height, area, ShadowMargin, card.X, card.Y, _anchor);
        if (_size != "pet") return position;
        var bottomGap = area.Bottom - (position.Top + ShadowMargin + card.Height);
        if (_dock == DockEdge.Bottom || (_dock is DockEdge.Left or DockEdge.Right && bottomGap <= Docking.Gap + 0.5))
            position.Top += bottomGap + (Pet.Child == _pet.Root ? PetControl.GroundInset : 0);
        return position;
    }

    private void ShowPetVersion(bool pixel)
    {
        FrameworkElement root = pixel ? (_pixelPet ??= new PixelPetControl()).Root : _pet.Root;
        if (Pet.Child == root) return;
        Pet.Child = root;
        _pixelPet?.Set(_petCounts);
        PlaceDocked();
    }

    private void UpdatePetLook()
    {
        if (_size != "pet" || _hwnd == IntPtr.Zero || Pet.ActualWidth <= 0 || PresentationSource.FromVisual(Pet) is null) return;

        // PointToScreen i Bounds są w pikselach urządzenia także na monitorach o różnym DPI.
        var center = Pet.PointToScreen(new System.Windows.Point(Pet.ActualWidth / 2, Pet.ActualHeight / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)center.X, (int)center.Y)).Bounds;
        var position = (center.X - screen.Left) / screen.Width;
        _pet.LookToward(position < 0.4 ? 1 : position > 0.6 ? -1 : 0);
    }

    private void DockTo(DockEdge edge)
    {
        ClosePanel(collapseIsland: false);
        _dock = edge;
        _anchor = null;
        ApplyLayout();
        SaveSettings();
    }

    private void SetDefaultPosition()
    {
        // Karta 8 px od prawej krawędzi obszaru roboczego; okno jest szersze o margines na cień.
        var area = SystemParameters.WorkArea;
        _dock = DockEdge.Right;
        _anchor = null;
        ApplyLayout();
        Left = area.Right - ActualWidth + ShadowMargin - Docking.Gap;
        Top = area.Top + 120;
    }

    // Po upuszczeniu: przy krawędzi widżet się do niej przykleja (u góry i u dołu jako wyspa),
    // gdzie indziej zostaje swobodny i pionowy.
    private void SnapAfterDrag()
    {
        var card = CardBox();
        var area = WorkArea();
        var edge = Docking.Decide(card, area);
        var wasHorizontal = Docking.IsHorizontal(_dock);
        _dock = edge;
        _anchor = Docking.IsHorizontal(edge) ? card.CenterX : null;
        ApplyLayout();
        if (edge == DockEdge.None && wasHorizontal)
        {
            // Wyspa odciągnięta od krawędzi wraca do karty pod tym samym środkiem, w całości na ekranie.
            var width = ActualWidth - 2 * ShadowMargin;
            var height = ActualHeight - 2 * ShadowMargin;
            Left = Math.Clamp(card.CenterX - width / 2, area.X, Math.Max(area.X, area.Right - width)) - ShadowMargin;
            Top = Math.Clamp(card.Y, area.Y, Math.Max(area.Y, area.Bottom - height)) - ShadowMargin;
        }
    }

    // Zmiana rozdzielczości, skalowania albo paska zadań: przyklejony widżet wraca na swoją krawędź.
    private void OnDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Safely("zmiana ekranu", PlaceDocked));

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea)) OnDisplayChanged(sender, e);
    }

    // Panel stoi obok karty (po stronie ekranu) albo — przy wyspie — pod nią lub nad nią, wyśrodkowany.
    // Rozmiary przychodzą w pikselach urządzenia i bez marginesu panelu (miejsca na cień), a zwrócony
    // punkt to lewy górny róg razem z marginesem — stąd odejmowany margines.
    private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlacePanel(System.Windows.Size popup, System.Windows.Size target, System.Windows.Point offset)
    {
        var scale = DeviceScale();
        var margin = PanelMargin * scale;
        const double besideGap = 4, islandGap = 6;
        System.Windows.Controls.Primitives.CustomPopupPlacement At(double x, double y, System.Windows.Controls.Primitives.PopupPrimaryAxis axis) =>
            new(new System.Windows.Point(x - margin, y - margin), axis);
        var centerX = (target.Width - popup.Width) / 2;
        var below = At(centerX, target.Height + islandGap * scale, System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal);
        var above = At(centerX, -popup.Height - islandGap * scale, System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal);
        // Obok karty panel zaczyna się 16 px niżej niż ona — tak stał od początku.
        var left = At(-popup.Width - besideGap * scale, margin, System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical);
        var right = At(target.Width + besideGap * scale, margin, System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical);
        return _dock switch
        {
            DockEdge.Top => [below, above],
            DockEdge.Bottom => [above, below],
            DockEdge.Left => [right, left],
            _ => [left, right],
        };
    }

    private void SaveSettings()
    {
        _config = _config with
        {
            Left = Left,
            Top = Top,
            Size = _size,
            Dock = _dock == DockEdge.None ? null : Docking.ToText(_dock),
            DockAnchor = Docking.IsHorizontal(_dock) ? _anchor : null,
        };
        try
        {
            WidgetConfigStore.Write(_paths.ConfigFile, _config);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WidgetLog.Write(_paths, $"zapis ustawień: {error.Message}");
        }
    }

    // Przyklejony widżet wraca na ten sam ekran, a gdy tego ekranu już nie ma (albo krawędź wpisano
    // ręcznie, bez pozycji) — na tę samą krawędź ekranu głównego. Obszar podaje się wprost: okno stoi
    // jeszcze tam, gdzie postawił je start, więc Screen.FromHandle wskazałby nie ten ekran.
    private void RestoreDocked(WidgetConfig config)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var scale = DeviceScale();
        Box Dips(System.Drawing.Rectangle r) => new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);
        var index = config.Left is double savedLeft && config.Top is double savedTop
            ? Docking.SavedScreen(_dock, savedLeft, savedTop, _anchor, ShadowMargin, screens.Select(screen => Dips(screen.Bounds)).ToList())
            : -1;
        Box area;
        if (index >= 0)
        {
            area = Dips(screens[index].WorkingArea);
            Top = config.Top!.Value;
        }
        else
        {
            var primary = screens.FirstOrDefault(screen => screen.Primary) ?? screens[0];
            area = Dips(primary.WorkingArea);
            _anchor = null;
            Top = area.Y + 120 - ShadowMargin;
        }
        var card = CardBox();
        (Left, Top) = PlaceWidget(card, area);
    }

    private void RestoreSettings()
    {
        var config = WidgetConfigStore.Read(_paths.ConfigFile) ?? new WidgetConfig();
        var size = config.Size is "mini" or "full" or "pet" ? config.Size : "mini";
        _dock = config.DockEdge;
        _anchor = config.DockAnchor;
        SetSize(size, false);
        ApplyConfig(config, save: false);

        if (_dock != DockEdge.None)
        {
            RestoreDocked(config);
            return;
        }

        // Swobodna karta (także plik sprzed krawędzi) — dokładnie jak dotąd. Zapisana pozycja może
        // wskazywać na odłączony monitor — wtedy wraca domyślna.
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (config.Left is double configLeft && config.Top is double configTop &&
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
        if (_config.HidesOnFullscreen && foreground != IntPtr.Zero && foreground != _hwnd &&
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
        // Wyspa rozwija się razem z panelem; jej wysokość się nie zmienia, więc panel od razu stoi dobrze.
        if (ShowsIsland) SetIslandExpanded(true, animate: true);
        Panel.IsOpen = true;
        Safely("panel", UpdateView);
        Safely("statusline", UpdateStatusLineHint);
        _hoverTimer.Start();
    }

    // Bez danych ze statusline (cudza, której instalator nie mógł bezpiecznie podpiąć) nie ma kontekstu
    // sesji — panel mówi dlaczego i jak to naprawić. Limity konta przychodzą wtedy z serwera.
    private void UpdateStatusLineHint()
    {
        var kind = ClaudeSettings.Inspect(ClaudeSettings.DefaultPath);
        StatusLineHint.Visibility = kind is StatusLineKind.Foreign or StatusLineKind.Missing ? Visibility.Visible : Visibility.Collapsed;
    }

    // Drgnięcie przesunięcia to jedyny sposób, żeby otwarty Popup przeliczył swoje położenie.
    private void RepositionPanel()
    {
        if (!Panel.IsOpen) return;
        Panel.HorizontalOffset += 0.1;
        Panel.HorizontalOffset -= 0.1;
    }

    private void ClosePanel(bool collapseIsland = true)
    {
        _pinned = false;
        PanelBody.BorderBrush = Brushes.Brush("#14FFFFFF");
        Panel.IsOpen = false;
        _hoverTimer.Stop();
        if (collapseIsland && Docking.IsHorizontal(_dock) && _size == "mini") SetIslandExpanded(false, animate: true);
    }

    // --- menu ---------------------------------------------------------------------------------

    private void BuildMenus()
    {
        var menu = new ContextMenu();
        var viewItem = new MenuItem { Header = "Widok" };
        var skinItem = new MenuItem { Header = "Strój Peta" };
        var traySkin = new ToolStripMenuItem("Strój Peta");
        foreach (var (key, label) in PetSkins.All)
        {
            var item = new MenuItem { Header = label };
            var trayItem = new ToolStripMenuItem(label);
            item.Click += (_, _) => Safely("strój Peta", () => ChangeConfig(c => c with { PetSkin = key }));
            trayItem.Click += (_, _) => Safely("strój Peta", () => ChangeConfig(c => c with { PetSkin = key }));
            skinItem.Items.Add(item);
            traySkin.DropDownItems.Add(trayItem);
            _skinItems.Add((key, item, trayItem));
        }
        foreach (var (key, label) in Views)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Safely("widok", () => SwitchSize(key));
            viewItem.Items.Add(item);
            _viewItems[key] = item;
        }
        var dockItem = new MenuItem { Header = "Przyklej do" };
        var trayDock = new ToolStripMenuItem("Przyklej do");
        foreach (var (edge, label) in new[]
        {
            (DockEdge.Right, "Prawej krawędzi"),
            (DockEdge.Left, "Lewej krawędzi"),
            (DockEdge.Top, "Górnej krawędzi (wyspa)"),
            (DockEdge.Bottom, "Dolnej krawędzi (wyspa)"),
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Safely("przyklejanie", () => DockTo(edge));
            dockItem.Items.Add(item);
            var trayItem = new ToolStripMenuItem(label);
            trayItem.Click += (_, _) => Safely("zasobnik: przyklejanie", () =>
            {
                if (_userHidden) { _userHidden = false; UpdateVisibility(); }
                DockTo(edge);
            });
            trayDock.DropDownItems.Add(trayItem);
            _dockItems.Add((edge, item, trayItem));
        }
        var hideItem = new MenuItem { Header = "Ukryj (przywrócisz z zasobnika)" };
        hideItem.Click += (_, _) => { _userHidden = true; UpdateVisibility(); };
        var closeItem = new MenuItem { Header = "Zamknij widżet" };
        closeItem.Click += (_, _) => Close();
        // IsCheckable przełącza znaczek sam, zanim przyjdzie Click — stąd odczyt IsChecked.
        _soundsItem = new MenuItem { Header = "Dźwięki", IsCheckable = true };
        _soundsItem.Click += (_, _) => Safely("dźwięki", () => ChangeConfig(c => c with { Sounds = _soundsItem.IsChecked }));
        _notificationsItem = new MenuItem { Header = "Powiadomienia Windows", IsCheckable = true };
        _notificationsItem.Click += (_, _) => Safely("powiadomienia", () => ChangeConfig(c => c with { Notifications = _notificationsItem.IsChecked }));
        _muteItem = new MenuItem { Header = "Wycisz" };
        foreach (var (duration, label) in MuteOptions.All)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Safely("wyciszenie", () => Mute(duration));
            _muteItem.Items.Add(item);
        }
        _muteItem.Items.Add(new Separator());
        _unmuteItem = new MenuItem { Header = "Wyłącz wyciszenie" };
        _unmuteItem.Click += (_, _) => Safely("wyciszenie", () => ChangeConfig(c => c with { MutedUntil = null }));
        _muteItem.Items.Add(_unmuteItem);
        var settingsItem = new MenuItem { Header = "Ustawienia…" };
        settingsItem.Click += (_, _) => Safely("ustawienia", OpenSettings);
        menu.Items.Add(viewItem);
        menu.Items.Add(skinItem);
        menu.Items.Add(_soundsItem);
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(_muteItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        foreach (var item in new[] { dockItem, hideItem, closeItem }) menu.Items.Add(item);
        foreach (var surface in new[] { Card, Mini, Island, Pet }) surface.ContextMenu = menu;

        _tray = new NotifyIcon();
        var trayMenu = new ContextMenuStrip();
        // Wersja jako nieklikalny nagłówek: tooltip ikony ma limit 63 znaków i pokazuje stan sesji.
        trayMenu.Items.Add(new ToolStripMenuItem($"Claude Code widget {_updates.CurrentVersion}") { Enabled = false });
        trayMenu.Items.Add(new ToolStripSeparator());
        _trayShow = (ToolStripMenuItem)trayMenu.Items.Add("Ukryj widżet");
        _trayShow.Click += (_, _) => Safely("zasobnik: pokaż/ukryj", () => { _userHidden = !_userHidden; UpdateVisibility(); });
        var trayView = new ToolStripMenuItem("Widok");
        foreach (var (key, label) in Views)
        {
            var item = new ToolStripMenuItem(label);
            item.Click += (_, _) => Safely("zasobnik: widok", () => { if (_userHidden) { _userHidden = false; UpdateVisibility(); } SwitchSize(key); });
            trayView.DropDownItems.Add(item);
            _trayViewItems[key] = item;
        }
        trayMenu.Items.Add(trayView);
        trayMenu.Items.Add(traySkin);
        trayMenu.Items.Add(trayDock);
        _trayAutostart = new ToolStripMenuItem("Uruchamiaj przy logowaniu") { Checked = AutostartService.IsEnabled() };
        _trayAutostart.Click += (_, _) => Safely("zasobnik: autostart", () => SetAutostart(!_trayAutostart.Checked));
        trayMenu.Items.Add(_trayAutostart);
        _traySounds = new ToolStripMenuItem("Dźwięki");
        _traySounds.Click += (_, _) => Safely("zasobnik: dźwięki", () => ChangeConfig(c => c with { Sounds = !c.SoundsOn }));
        trayMenu.Items.Add(_traySounds);
        _trayNotifications = new ToolStripMenuItem("Powiadomienia Windows");
        _trayNotifications.Click += (_, _) => Safely("zasobnik: powiadomienia", () => ChangeConfig(c => c with { Notifications = !c.NotificationsOn }));
        trayMenu.Items.Add(_trayNotifications);
        _trayMute = new ToolStripMenuItem("Wycisz");
        foreach (var (duration, label) in MuteOptions.All)
        {
            _trayMute.DropDownItems.Add(label, null, (_, _) => Safely("zasobnik: wyciszenie", () => Mute(duration)));
        }
        _trayMute.DropDownItems.Add(new ToolStripSeparator());
        _trayUnmute = new ToolStripMenuItem("Wyłącz wyciszenie");
        _trayUnmute.Click += (_, _) => Safely("zasobnik: wyciszenie", () => ChangeConfig(c => c with { MutedUntil = null }));
        _trayMute.DropDownItems.Add(_trayUnmute);
        trayMenu.Items.Add(_trayMute);
        trayMenu.Items.Add("Ustawienia…", null, (_, _) => Safely("zasobnik: ustawienia", OpenSettings));
        trayMenu.Items.Add(new ToolStripSeparator());
        // Uruchomienie deweloperskie nie ma skąd się aktualizować — pozycja jest, ale wyszarzona.
        _trayCheckItem = new ToolStripMenuItem(_updates.IsInstalled ? CheckUpdatesLabel : "Sprawdź aktualizacje (tylko po instalacji)")
        {
            Enabled = _updates.IsInstalled,
        };
        _trayCheckItem.Click += async (_, _) => await Safely2("zasobnik: sprawdzanie aktualizacji", CheckUpdatesManuallyAsync);
        trayMenu.Items.Add(_trayCheckItem);
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
            // Pobrana wersja nie zmieni się do restartu — kolejne sprawdzanie nic by nie dało.
            _trayCheckItem.Visible = false;
        });
    }

    // Ręczne sprawdzenie: nie trzeba czekać do 6 h na sprawdzenie okresowe. Wynik w dymku, bo menu
    // zamyka się po kliknięciu; restartu nie robi samo — to nadal osobna pozycja w menu.
    private async Task CheckUpdatesManuallyAsync()
    {
        _trayCheckItem.Enabled = false;
        _trayCheckItem.Text = "Sprawdzam aktualizacje…";
        UpdateCheckResult result;
        try
        {
            result = await _updates.CheckAsync(message => WidgetLog.Write(_paths, message));
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _trayCheckItem.Enabled = true;
                _trayCheckItem.Text = CheckUpdatesLabel;
            });
        }
        var (text, icon) = result switch
        {
            UpdateCheckResult.Ready => ($"Pobrano wersję {_updates.PendingVersion}. Zainstaluje się przy następnym uruchomieniu albo od razu z menu ikony.", System.Windows.Forms.ToolTipIcon.Info),
            UpdateCheckResult.UpToDate => ($"Masz najnowszą wersję ({_updates.CurrentVersion}).", System.Windows.Forms.ToolTipIcon.Info),
            _ => ("Nie udało się sprawdzić aktualizacji. Szczegóły w widget.log.", System.Windows.Forms.ToolTipIcon.Warning),
        };
        await Dispatcher.InvokeAsync(() => _tray.ShowBalloonTip(5000, "Claude Code widget", text, icon));
    }

    // --- zdarzenia okna -------------------------------------------------------------------------

    private void WireEvents()
    {
        foreach (var surface in new[] { Card, Mini, Island, Pet })
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
                    ClosePanel(collapseIsland: false);
                    SnapAfterDrag();
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
        MinimizeButton.Click += (_, _) => Safely("zmniejszanie", () => SwitchSize("mini"));
        ExpandButton.Click += (_, _) => Safely("rozwijanie", () => SwitchSize("full"));
        IslandSizeButton.Click += (_, _) => Safely("wyspa: rozmiar", () => SwitchSize(_size == "mini" ? "full" : "mini"));
        LimitModeToggle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Safely("limity: tryb", () =>
            {
                ChangeConfig(c => c with { LimitDisplay = LimitDisplay.Key(LimitDisplay.Toggle(c.LimitMode)) });
                UpdateView();
            });
        };
        Panel.CustomPopupPlacementCallback = PlacePanel;
        // Popup nie przelicza położenia, gdy sam rośnie (lista sesji wypełnia się po otwarciu — nad wyspą
        // przy dolnej krawędzi nachodziłby na nią) ani gdy okno się przesuwa (wyspa rozwijana przy boku
        // ekranu przesuwa swój środek).
        PanelBody.SizeChanged += (_, _) => RepositionPanel();
        Pet.Loaded += (_, _) => UpdatePetLook();
        LocationChanged += (_, _) =>
        {
            RepositionPanel();
            UpdatePetLook();
        };
        DpiChanged += (_, _) => UpdatePetLook();
        // Rozwijanie wyspy i zmiana widoku zmieniają rozmiar okna — przyklejone trzyma swoje miejsce.
        SizeChanged += (_, _) => Safely("położenie", PlaceDocked);

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
            Safely("wyciszenie", () => ExpireMute(NowMs()));
        };
        _configDebounce.Tick += (_, _) => { _configDebounce.Stop(); Safely("ustawienia z pliku", ReloadConfig); };
        MouseEnter += (_, _) => UpdateOpacity();
        MouseLeave += (_, _) => UpdateOpacity();
        _agentsTimer.Tick += async (_, _) => await Task.WhenAll(
            Safely2("lista sesji", RunAgentsTickAsync),
            Safely2("nazwy z Orca", RefreshOrcaLabelsAsync));
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
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        _tray.Visible = true;
        Safely("start", UpdateView);
        Safely("sprzątanie", () => _aggregator.CleanupOrphans());
        Safely("obserwacja katalogu", StartWatcher);
        Safely("obserwacja ustawień", StartConfigWatcher);
        _refreshTimer.Start();
        _agentsTimer.Start();
        _cleanupTimer.Start();
        _ = _updates.StartAsync(message => WidgetLog.Write(_paths, message), CancellationToken.None);
        // Limity z serwera: widoczne także bez statusline (Orca, IDE, tryb -p), bez konfiguracji.
        _limitsPoller = LimitsPoller.Create(_paths, UserAgent, message => WidgetLog.Write(_paths, message));
        _limitsPoller.Start();
        _ = Task.Run(() => Safely("rozszerzenie VS Code", () => VsCodeExtensionInstaller.InstallIfNeeded(_paths, message => WidgetLog.Write(_paths, message))));
        // Tylko zainstalowany widżet: uruchomienie deweloperskie wpisałoby swoją ścieżkę do settings.json.
        if (_updates.IsInstalled) _ = Task.Run(InstallHooks.ReconcileSettings);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _agentsTimer.Stop();
        _cleanupTimer.Stop();
        _configDebounce.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        _limitsPoller?.Dispose();
        _watcher?.Dispose();
        _configWatcher?.Dispose();
        _hotkey?.Dispose();
        _settingsWindow?.Close();
        _notifier.ClearAll();
        _tray.Visible = false;
        _tray.Dispose();
        if (_trayIcon is not null) TrayIconFactory.Destroy(_trayIcon);
    }
}
