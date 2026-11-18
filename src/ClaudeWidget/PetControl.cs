using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClaudeWidget.Core.Settings;

namespace ClaudeWidget;

/// <summary>Animowany monitor CRT pokazujący najpilniejszy stan sesji.</summary>
public sealed partial class PetControl : FrameworkElement
{
    private enum Mood { Idle, Working, Done, Waiting }

    // Nowy wynik potrafi czekać godzinami: Pet cieszy się dwa razy, potem tylko spokojnie stoi.
    private const double CelebrationPeriod = 3.4, CelebrationLength = 0.83, Celebrations = 2;
    private const double WavePeriod = 2.15, WaveLength = 1.22;
    // Włączenie kineskopu przy zmianie stanu: obraz rozwija się z jasnej linii.
    private const double PowerOn = 0.16;
    private const double TypingStep = 0.09;
    private const double GestureFrame = 0.033, CalmFrame = 0.145;
    private const double BaseHeight = 142;
    private static readonly DependencyProperty LookDirectionProperty = DependencyProperty.Register(
        "LookDirection", typeof(double), typeof(PetControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Ink = Paint("#131D31");
    private static readonly Brush Porcelain = Paint("#FFF2D8");
    private static readonly Brush Gray = Paint("#BDC9E3");
    private static readonly Brush Amber = Paint("#FFD078");
    private static readonly Brush Mint = Paint("#82F2B4");
    private static readonly Brush Coral = Paint("#FF846F");
    private static readonly Pen Outline = Stroke(Ink, 2.2);
    private static readonly Pen SleepOutline = Stroke(Ink, 3.4);
    private static readonly Pen SleepInk = Stroke(Porcelain, 1.55);
    private static readonly Brush TerminalHistory = Paint("#A78353");
    private static readonly Pen PromptInk = Stroke(Amber, 1.45);
    private static readonly Pen SmileInk = Stroke(Mint, 2.4);

    private static readonly Geometry Prompt = Path("M43,53 L46,55.5 L43,58");
    private static readonly int[][] TerminalRows = [[7, 5, 8], [10, 6], [5, 8, 4], [8, 9, 5]];
    private static readonly Geometry HappyEyes = Path("M48,48 Q52,40.5 56,48 M68,48 Q72,40.5 76,48");
    private static readonly Geometry Smile = Path("M56,52 H68 Q66,59 62,59 Q58,59 56,52 Z");
    private static readonly Geometry AttentionMark = Path("M58.65,35.5 H65.35 L64.55,51.4 H59.45 Z M59.15,55 H64.85 V60.7 H59.15 Z");
    private static readonly Geometry SpeechBubble = Path("M84,4 H94 Q102,4 102,12 V20 Q102,27 95,27 H87 L80,31 L81,26 Q76,24 76,19 V12 Q76,4 84,4 Z");
    private static readonly Geometry Check = Path("M83.5,15.5 L87.5,19.5 L94.5,11.5");

    private static readonly Geometry[] CounterDigits = "123456789+".Select(ch =>
    {
        var text = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            11, Ink, 1);
        var geometry = text.BuildGeometry(new Point((18 - text.Width) / 2, (17 - text.Height) / 2));
        geometry.Freeze();
        return geometry;
    }).ToArray();
    private static readonly DrawingGroup Shadow = Art(dc =>
    {
        var brush = new RadialGradientBrush(Color.FromArgb(120, 12, 17, 30), Color.FromArgb(0, 12, 17, 30));
        dc.DrawEllipse(brush, null, new Point(57, 136), 29, 4.5);
    });
    private static readonly DrawingGroup SleepMarks = Art(dc =>
    {
        var marks = Path("M81,16 H87 L81,22 H87 M92,6 H99 L92,13 H99");
        dc.DrawGeometry(null, SleepOutline, marks);
        dc.DrawGeometry(null, SleepInk, marks);
    });

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    // _motion liczy od zmiany stanu (gesty, terminal), _ambient płynie bez przerw (oddech, mruganie, kołysanie),
    // więc zmiana stanu nie szarpie spokojnymi ruchami.
    private readonly Stopwatch _motion = new();
    private readonly Stopwatch _ambient = new();
    private Mood _mood;
    private string _skin = "classic";
    private Outfit? _outfit;
    private int _waiting, _done;
    private int _lookTarget;
    // Strona ekranu, w którą Pet jest zwrócony. Zostaje po powrocie na środek; zmienia się tylko po drugiej stronie zera,
    // gdzie poza jest symetryczna, więc odbicie jest niewidoczne.
    private int _facing = 1;
    // Strona ekranu ręki, która macha: bliższa (przeciwna do patrzenia), więc nie chowa się za tułowiem i głową.
    // Ustalana przy wejściu w czekanie; potem zmienia się tylko w przerwie między gestami, gdy ręka jest opuszczona.
    private int _waveSide = -1;

    public PetControl()
    {
        Width = 116;
        Height = BaseHeight;
        ClipToBounds = true;
        _timer.Tick += (_, _) =>
        {
            InvalidateVisual();
            ScheduleFrame();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();
        Loaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            UpdateTimer();
        };
        Unloaded += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            _timer.Stop();
            _motion.Stop();
            _ambient.Stop();
        };
    }

    public FrameworkElement Root => this;
    internal const double GroundInset = 4.9;

    internal void SetSkin(string? key)
    {
        var skin = PetSkins.Normalize(key);
        if (_skin == skin) return;
        _skin = skin;
        _outfit = skin switch
        {
            "portfolio" => Clothes.Portfolio,
            "christmas" => Clothes.Christmas,
            _ => null,
        };
        Height = BaseHeight + (_outfit?.Headroom ?? 0);
        InvalidateVisual();
    }

    internal void LookToward(int direction)
    {
        if (direction == _lookTarget) return;
        var previous = (double)GetValue(LookDirectionProperty);
        _lookTarget = direction;
        SetValue(LookDirectionProperty, (double)direction);
        if (IsVisible && SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(LookDirectionProperty, new DoubleAnimation(previous, direction, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        }
        else BeginAnimation(LookDirectionProperty, null);
    }

    /// <summary>Liczby sesji: czeka / pracuje / gotowe. Pet pokazuje najwyższy priorytet.</summary>
    public void Set(IReadOnlyDictionary<string, int> counts)
    {
        var waiting = counts.GetValueOrDefault("czeka");
        var done = counts.GetValueOrDefault("gotowe");
        var mood = waiting > 0 ? Mood.Waiting
            : counts.GetValueOrDefault("pracuje") > 0 ? Mood.Working
            : done > 0 ? Mood.Done : Mood.Idle;
        if (mood == _mood && waiting == _waiting && done == _done) return;

        if (mood != _mood)
        {
            _motion.Reset();
            _waveSide = -(_lookTarget != 0 ? _lookTarget : _facing);
        }
        _mood = mood;
        _waiting = waiting;
        _done = done;
        UpdateTimer();
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.InvokeAsync(UpdateTimer);
    }

    private void UpdateTimer()
    {
        _timer.Stop();
        _motion.Stop();
        _ambient.Stop();
        if (IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            _motion.Start();
            _ambient.Start();
            ScheduleFrame();
            _timer.Start();
        }
        else BeginAnimation(LookDirectionProperty, null);
        InvalidateVisual();
    }

    /// <summary>Gęste klatki tylko w trakcie gestu; przerwę kończymy dokładnie na jego początku.</summary>
    private void ScheduleFrame()
    {
        var time = _motion.Elapsed.TotalSeconds;
        var next = _mood switch
        {
            // Terminal pisze co 90 ms: trafiamy w każdy krok, bez podwójnych przeskoków.
            Mood.Working => TypingStep - time % TypingStep + 0.003,
            Mood.Waiting => UntilNextFrame(time % WavePeriod, WaveLength, WavePeriod),
            Mood.Done when time < Celebrations * CelebrationPeriod =>
                UntilNextFrame(time % CelebrationPeriod, CelebrationLength, CelebrationPeriod),
            _ => CalmFrame,
        };
        if (time < PowerOn) next = Math.Min(next, GestureFrame);
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(next, 0.016, CalmFrame));
    }

    private static double UntilNextFrame(double phase, double length, double period) =>
        phase < length ? GestureFrame : Math.Min(CalmFrame, period - phase + 0.002);

    protected override void OnRender(DrawingContext dc)
    {
        var animate = SystemParameters.ClientAreaAnimation;
        DrawFrame(dc, animate ? _motion.Elapsed.TotalSeconds : 0, animate ? _ambient.Elapsed.TotalSeconds : 0,
            (double)GetValue(LookDirectionProperty), animate);
    }

    private void DrawFrame(DrawingContext dc, double time, double ambient, double look, bool animate)
    {
        dc.PushTransform(new TranslateTransform(0, Height - BaseHeight));
        var turn = Math.Abs(look);
        if (look > 0.001) _facing = 1;
        else if (look < -0.001) _facing = -1;
        if (_waveSide != -_facing && (!animate || _mood != Mood.Waiting || time % WavePeriod >= WaveLength))
            _waveSide = -_facing;

        var jump = !animate ? Jump.None : _mood switch
        {
            Mood.Waiting => WaitingJump(time % WavePeriod),
            Mood.Done when time < Celebrations * CelebrationPeriod => Jump.At(time % CelebrationPeriod, 0.5, 4),
            _ => Jump.None,
        };
        var color = _mood switch
        {
            Mood.Waiting => Coral,
            Mood.Working => Amber,
            Mood.Done => Mint,
            _ => Gray,
        };

        // Cień zostaje na ziemi: w powietrzu maleje i blednie.
        var air = jump.Lift / 5;
        dc.PushTransform(new ScaleTransform(1 - 0.25 * air, 1 - 0.25 * air, 57, 136));
        dc.PushOpacity(1 - 0.4 * air);
        dc.DrawDrawing(Shadow);
        dc.Pop();
        dc.Pop();

        dc.PushTransform(new TranslateTransform(0, -jump.Lift));
        dc.PushTransform(new ScaleTransform(jump.ScaleX, jump.ScaleY, 58, 136));

        // W przestrzeni ciała Pet zawsze patrzy w prawo; lewą stronę daje lustro. Rysunki dostają drugie lustro,
        // żeby światło padało z tej samej strony co na głowie.
        dc.PushTransform(new ScaleTransform(_facing * (1 - 0.12 * turn), 1, 58, 136));
        // Prawe ramię ciała stoi po stronie ekranu, w którą Pet patrzy.
        var rightWave = _waveSide == _facing ? 1 : 0;
        var leftWave = 1 - rightWave;
        DrawLeg(dc, 66.5, -jump.Lift * 2.2, turn, far: true);
        DrawLeg(dc, 49.5, jump.Lift * 1.5, turn, far: false);
        DrawAnimatedArm(dc, true, time, ambient, animate, turn, rightWave);
        if (turn <= 0.01) DrawAnimatedArm(dc, false, time, ambient, animate, turn, leftWave);
        DrawLit(dc, _outfit?.BodyBack ?? Artwork.BodyBack, 58);
        if (_outfit is not null)
        {
            dc.PushTransform(new TranslateTransform(-4.5 * turn, 2.5));
            dc.PushTransform(new ScaleTransform(1 - 0.06 * turn, 1, 58, 85));
            DrawLit(dc, _outfit.Hood, 58);
            dc.Pop();
            dc.Pop();
        }
        dc.PushTransform(new TranslateTransform(3.6 * turn, 0));
        dc.PushTransform(new ScaleTransform(1 - 0.10 * turn, 1, 58, 95));
        DrawLit(dc, _outfit?.BodyFront ?? Artwork.BodyFront, 58);
        dc.DrawEllipse(color, null, new Point(58 + 0.5 * _facing, 94.9), 2.6, 2.6);
        dc.Pop();
        dc.Pop();
        if (turn > 0.01) DrawAnimatedArm(dc, false, time, ambient, animate, turn, leftWave);
        dc.Pop();

        DrawHead(dc, time, ambient, look, animate);
        dc.Pop();
        dc.Pop();

        if (_mood != Mood.Waiting)
        {
            dc.PushTransform(new TranslateTransform(12, 0));
            DrawSignal(dc, color, time, ambient, animate);
            dc.Pop();
        }
        var single = (_waiting == 1 && _done == 0) || (_done == 1 && _waiting == 0);
        if (!single || _mood == Mood.Working)
        {
            if (_waiting > 0) DrawCounter(dc, 2, _waiting, Coral);
            if (_done > 0) DrawCounter(dc, 22, _done, Mint);
        }
        dc.Pop();
    }

    /// <summary>Rysunek części ciała z kompensacją lustra, żeby nie odwracać kierunku światła.</summary>
    private void DrawLit(DrawingContext dc, Drawing drawing, double axis)
    {
        dc.PushTransform(new ScaleTransform(_facing, 1, axis, 0));
        dc.DrawDrawing(drawing);
        dc.Pop();
    }

    /// <summary>Podskok z wagą: przysiad, szybkie wybicie, dłuższy szczyt i ugięcie przy lądowaniu.</summary>
    private readonly record struct Jump(double Lift, double ScaleX, double ScaleY)
    {
        public static readonly Jump None = new(0, 1, 1);
        private const double Crouch = 0.07, Landing = 0.08;

        public static Jump At(double time, double airTime, double height)
        {
            if (time < 0) return None;
            if (time < Crouch) return Squash(time / Crouch, 0.06);
            time -= Crouch;
            if (time < airTime)
            {
                var progress = time / airTime;
                var stretch = 0.035 * Math.Pow(Math.Cos(progress * Math.PI), 2);
                return new(height * Math.Sin(Math.PI * Math.Pow(progress, 0.8)), 1 - stretch * 0.6, 1 + stretch);
            }
            time -= airTime;
            return time < Landing ? Squash(time / Landing, 0.07) : None;
        }

        private static Jump Squash(double progress, double depth)
        {
            var amount = Math.Sin(progress * Math.PI) * depth;
            return new(0, 1 + amount * 0.7, 1 - amount);
        }
    }

    /// <summary>Czekanie ma własny rytm: dwa podskoki pod rząd, drugi niższy.</summary>
    private static Jump WaitingJump(double phase) =>
        phase < 0.5 ? Jump.At(phase - 0.02, 0.3, 5) : Jump.At(phase - 0.5, 0.24, 3);

    private static double Hop(double time, double period, double duration)
    {
        var phase = Math.Min(1, time % period / duration);
        return Math.Sin(phase * Math.PI);
    }

    private void DrawAnimatedArm(DrawingContext dc, bool right, double time, double ambient, bool animate, double turn, double waveSide)
    {
        var sway = animate ? Math.Sin(ambient * 2.1) : 0;
        var wave = _mood == Mood.Waiting ? Gesture(time % WavePeriod, WaveLength, animate) * waveSide : 0;
        var cheer = _mood == Mood.Done && (!animate || time < Celebrations * CelebrationPeriod)
            ? Gesture(time % CelebrationPeriod, CelebrationLength, animate) : 0;
        var raise = Math.Max(wave, cheer);
        var waveShare = raise > 0 ? wave / raise : 0;
        var wrist = wave * Math.Sin(ambient * 16) * 20;
        // Ciało zwrócone w prawo pokazuje lewy bok: lewe ramię jest bliżej, prawe dalej.
        var near = !right;
        var side = right ? 1 : -1;
        // Barki symetryczne względem osi ciała (58) i ta sama poza spoczynkowa: odbicie przy zmianie strony nic nie przesuwa.
        // Luźny rękaw unosi się ku kołnierzowi wraz z barkiem.
        var shoulderSpan = _outfit is null ? 16.5 : Lerp(19.2, 15, raise);
        var shoulderX = 58 + side * (shoulderSpan + (near ? 1.8 : -1.6) * turn);
        dc.PushTransform(new TranslateTransform(shoulderX, 86 + (near ? 3 : -1.2) * turn));
        // Kołysanie wahadłowe: obie ręce w tę samą stronę ekranu, niezależnie od lustra ciała i rysunku ręki.
        sway *= -side * _facing;
        DrawArm(dc, right, Lerp(12.5 + sway * 3.5, -80.5, raise), Lerp(5 - sway * 3.5, Lerp(-12.5, -29, waveShare), raise),
            Lerp(1.5 + sway * 5.5, -15.5, raise) + wrist, raise > 0.52);
        dc.Pop();
    }

    private static double Gesture(double phase, double duration, bool animate)
    {
        if (!animate) return 1;
        var amount = Math.Clamp(Math.Min(phase / 0.2, (duration - phase) / 0.29), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static double Lerp(double from, double to, double amount) => from + (to - from) * amount;

    private void DrawArm(DrawingContext dc, bool right, double shoulder, double elbow, double wrist, bool open)
    {
        dc.PushTransform(new ScaleTransform(right ? 1 : -1, 1));
        dc.PushTransform(new RotateTransform(shoulder));
        if (_outfit is null) dc.DrawDrawing(Artwork.UpperArm);
        else Clothes.DrawSleeve(dc, _outfit.Fabric, elbow);
        dc.PushTransform(new TranslateTransform(6, 12));
        dc.PushTransform(new RotateTransform(elbow));
        if (_outfit is null) dc.DrawDrawing(Artwork.Forearm);
        dc.PushTransform(new TranslateTransform(5, 13));
        dc.PushTransform(new RotateTransform(wrist));
        dc.DrawDrawing(open ? Artwork.OpenHand : Artwork.RelaxedHand);
        for (var i = 0; i < 6; i++) dc.Pop();
    }

    /// <summary>Z przodu obie nogi są takie same; przy skręcie czubki butów idą w stronę patrzenia, a dalsza noga ciemnieje.</summary>
    private void DrawLeg(DrawingContext dc, double x, double angle, double turn, bool far)
    {
        var shade = far ? 0.9 * turn : 0;
        dc.PushTransform(new TranslateTransform(x, 109));
        dc.PushTransform(new RotateTransform(angle));
        DrawLit(dc, _outfit?.Leg ?? Artwork.Leg, 0);
        if (shade > 0) DrawShade(dc, _outfit?.CuffShade ?? Artwork.CuffShade, shade);
        dc.PushTransform(new TranslateTransform(1.2 * turn, 0));
        dc.PushTransform(new SkewTransform(20 * turn, 0, 0, 18));
        dc.PushTransform(new ScaleTransform(1 + (far ? 0.12 : 0.22) * turn, 1, 0, 0));
        DrawLit(dc, _outfit?.Foot ?? Artwork.Foot, 0);
        if (shade > 0) DrawShade(dc, _outfit?.FootShade ?? Artwork.FootShade, shade);
        for (var i = 0; i < 5; i++) dc.Pop();
    }

    private void DrawShade(DrawingContext dc, Drawing drawing, double opacity)
    {
        dc.PushOpacity(opacity);
        DrawLit(dc, drawing, 0);
        dc.Pop();
    }

    private void DrawHead(DrawingContext dc, double time, double ambient, double look, bool animate)
    {
        var turn = Math.Abs(look);
        // Oddech tylko we śnie, wyraźny (1,2 px), żeby nie migotał na krawędziach.
        var breath = animate && _mood == Mood.Idle ? 1.2 * Math.Sin(ambient * 2.1) : 0;
        // Oś głowy (62 w rysunku) stoi nad osią ciała (58).
        dc.PushTransform(new TranslateTransform(-4 + 1.8 * look, breath));
        // Skręt, nie przechył: pionowe krawędzie zostają pionowe, dalsza strona idzie lekko w górę
        // (przy patrzeniu w prawo dalsza jest prawa; y' = y + tg(kąt)·(x − 62), więc kąt ujemny ją podnosi).
        dc.PushTransform(new SkewTransform(0, -3 * look, 62, 48));

        // Tył kineskopu jest dalej, więc mniejszy i wyżej; wysuwa się po stronie przeciwnej do patrzenia.
        // Front skraca się jak tułów (0,88–0,90), żeby głowa i ciało były skręcone o ten sam kąt.
        var back = Matrix.Identity;
        back.ScaleAt(1 - 0.14 * turn, 0.97 - 0.06 * turn, 62, 48);
        back.Translate(-14 * look, -3 - 2 * turn);
        var front = Matrix.Identity;
        front.ScaleAt(1 - 0.10 * turn, 1, 62, 77.5);
        Artwork.DrawHousingSides(dc, front, back);

        dc.PushTransform(new MatrixTransform(back));
        dc.DrawDrawing(Artwork.MonitorBack);
        dc.Pop();

        dc.PushTransform(new MatrixTransform(front));
        dc.DrawDrawing(Artwork.Monitor);
        var powerOn = animate && time < PowerOn ? time / PowerOn : 1;
        var unfold = 1 - Math.Pow(1 - powerOn, 3);
        dc.PushTransform(new ScaleTransform(1, 0.06 + 0.94 * unfold, 62, 47.5));
        if (_mood == Mood.Waiting)
        {
            dc.PushOpacity(animate ? 0.86 + 0.14 * Hop(time, WavePeriod, 0.94) : 1);
            dc.DrawGeometry(Coral, null, AttentionMark);
            dc.Pop();
        }
        else if (_mood == Mood.Working)
        {
            DrawTerminal(dc, time, animate);
        }
        else
        {
            DrawFace(dc, ambient, animate);
        }
        dc.Pop();
        if (powerOn < 1)
        {
            dc.PushOpacity(0.7 * (1 - powerOn));
            dc.DrawRoundedRectangle(Porcelain, null, new Rect(38, 46.6, 48, 1.8), 0.9, 0.9);
            dc.Pop();
        }
        if (_outfit?.Headroom > 0) dc.DrawDrawing(Hat.Drawing);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawFace(DrawingContext dc, double ambient, bool animate)
    {
        switch (_mood)
        {
            case Mood.Done:
                dc.DrawGeometry(null, SmileInk, HappyEyes);
                dc.DrawGeometry(Mint, null, Smile);
                break;
            default:
                var blink = animate && ambient % 5.2 >= 4.94;
                var eyeHeight = blink ? 1 : 2.75;
                var eyeTop = 46.4 - eyeHeight / 2;
                dc.DrawRoundedRectangle(Porcelain, null, new Rect(47, eyeTop, 10, eyeHeight), 0.55, 0.55);
                dc.DrawRoundedRectangle(Porcelain, null, new Rect(67, eyeTop, 10, eyeHeight), 0.55, 0.55);
                dc.DrawRectangle(Porcelain, null, new Rect(60, 54, 4, 1.6));
                break;
        }
    }

    private static void DrawTerminal(DrawingContext dc, double time, bool animate)
    {
        const double rowDuration = 1.32;
        var row = (int)(time / rowDuration % TerminalRows.Length);
        var phase = time % rowDuration;
        var width = animate ? 4 + 2 * Math.Floor(phase / TypingStep) : 27;
        DrawTerminalRow(dc, 38, TerminalRows[(row + 2) % TerminalRows.Length], 27, TerminalHistory);
        DrawTerminalRow(dc, 46, TerminalRows[(row + 3) % TerminalRows.Length], 27, TerminalHistory);
        dc.DrawGeometry(null, PromptInk, Prompt);
        var cursor = DrawTerminalRow(dc, 54, TerminalRows[row], width, Amber);
        if (!animate || phase < 0.94 || phase >= 1.12)
            dc.DrawRectangle(Amber, null, new Rect(cursor + 1.7, 52.7, 1.65, 5.3));
    }

    private static double DrawTerminalRow(DrawingContext dc, double y, int[] words, double width, Brush ink)
    {
        const double start = 50;
        var x = start;
        var end = start + width;
        foreach (var word in words)
        {
            var visible = Math.Min(word, end - x);
            if (visible <= 0) return end;
            dc.DrawRoundedRectangle(ink, null, new Rect(x, y, visible, 1.9), 0.6, 0.6);
            x += word + 3;
        }
        return Math.Min(end, x - 3);
    }

    private void DrawSignal(DrawingContext dc, Brush color, double time, double ambient, bool animate)
    {
        if (_mood == Mood.Idle)
        {
            dc.PushTransform(new TranslateTransform(0, -2.3 * (0.5 - 0.5 * Math.Cos(ambient * 2 * Math.PI / 3.4))));
            dc.DrawDrawing(SleepMarks);
            dc.Pop();
            return;
        }

        dc.DrawGeometry(color, Outline, SpeechBubble);
        switch (_mood)
        {
            case Mood.Working:
                // Kropki zmieniają się co 4 kroki pisania, w rytmie zegara.
                var dots = animate ? 1 + (int)(time / (4 * TypingStep)) % 3 : 3;
                for (var i = 0; i < 3; i++)
                {
                    dc.PushOpacity(i < dots ? 1 : 0.25);
                    dc.DrawEllipse(Ink, null, new Point(83.5 + i * 5.5, 15.5), 1.45, 1.45);
                    dc.Pop();
                }
                break;
            case Mood.Done:
                dc.DrawGeometry(null, Outline, Check);
                break;
        }
    }

    private static void DrawCounter(DrawingContext dc, double top, int count, Brush color)
    {
        dc.DrawRoundedRectangle(color, Outline, new Rect(2, top, 17, 17), 5.2, 5.2);
        dc.PushTransform(new TranslateTransform(1.5, top));
        dc.DrawGeometry(Ink, null, CounterDigits[Math.Min(count, 10) - 1]);
        dc.Pop();
    }

    private static void Shape(DrawingContext dc, string path, Brush? fill, Pen? pen = null) =>
        dc.DrawGeometry(fill, pen, Path(path));

    private static Geometry Path(string data)
    {
        var path = Geometry.Parse(data);
        path.Freeze();
        return path;
    }

    private static DrawingGroup Art(Action<DrawingContext> draw)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) draw(dc);
        group.Freeze();
        return group;
    }

    private static Brush Paint(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Brush Ramp(params string[] colors)
    {
        var stops = new GradientStopCollection();
        for (var i = 0; i < colors.Length; i++)
            stops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), (double)i / (colors.Length - 1)));
        var brush = new LinearGradientBrush(stops, new Point(0.25, 0), new Point(0.75, 1));
        brush.Freeze();
        return brush;
    }

    private static Pen Stroke(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
