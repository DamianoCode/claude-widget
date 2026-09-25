using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeWidget;

/// <summary>
/// Widok „pet”: pixel-artowa postać pokazuje najpilniejszy stan sesji zamiast trzech świateł — czeka
/// (podskakuje, macha, dymek „!”), pracuje (na ekranie <c>&gt;_</c>, dymek „…”), nowy wynik (cieszy się,
/// dymek „✓”), nic (drzemie, ulatują „z”). Każdą klatkę składa się w bitmapie 32×34 z prostokątów
/// i wzorków, a WPF powiększa ją bez wygładzania. Klatki podmienia zwykły timer kilka razy na sekundę
/// i tylko wtedy, gdy postać jest widoczna — przezroczyste okno WPF rysuje się całe przy każdej zmianie.
/// </summary>
public sealed class PetControl
{
    // Postać zaczyna się 3 px od lewej — z lewej stoją liczniki sesji do przejrzenia.
    private const int ArtWidth = 35, ArtHeight = 34, Zoom = 3, FigureLeft = 3;

    // Kolory jako 0xAARRGGBB — w pamięci to akurat kolejność Bgra32.
    private const uint Outline = 0xFF2B1A14, Shell = 0xFFD97757, ShellLight = 0xFFF0A583, ShellShade = 0xFFB4593D;
    private const uint Screen = 0xFF141414, Ink = 0xFF1A1A1A, Shadow = 0x55000000, Sleep = 0xFFB0B0B0;

    private static readonly Dictionary<string, (uint Color, TimeSpan Tick)> Moods = new()
    {
        ["czeka"] = (0xFFFF5A4E, TimeSpan.FromMilliseconds(120)),
        ["pracuje"] = (0xFFFFB224, TimeSpan.FromMilliseconds(160)),
        ["gotowe"] = (0xFF3DD68C, TimeSpan.FromMilliseconds(220)),
        ["idle"] = (0xFF8A8A8A, TimeSpan.FromMilliseconds(700)),
    };

    // Podskok przy „czeka” (w pikselach grafiki): w górę i z powrotem, potem chwila na ziemi.
    private static readonly int[] Bounce = [0, -1, -2, -2, -1, 0, 0, 0];

    private static readonly string[] Prompt = ["X..", ".X.", "..X", ".X.", "X.."];
    private static readonly string[] HappyEye = [".X.", "X.X"];
    private static readonly string[] Smile = ["X..X", ".XX."];
    private static readonly string[] Check = ["....XX", "X..XX.", "XXXX..", ".XX..."];
    private static readonly string[] Z = ["XXXX", "..X.", ".X..", "XXXX"];

    private static readonly Dictionary<char, string[]> Digits = new()
    {
        ['1'] = [".X.", "XX.", ".X.", ".X.", "XXX"],
        ['2'] = ["XXX", "..X", "XXX", "X..", "XXX"],
        ['3'] = ["XXX", "..X", "XXX", "..X", "XXX"],
        ['4'] = ["X.X", "X.X", "XXX", "..X", "..X"],
        ['5'] = ["XXX", "X..", "XXX", "..X", "XXX"],
        ['6'] = ["XXX", "X..", "XXX", "X.X", "XXX"],
        ['7'] = ["XXX", "..X", "..X", "..X", "..X"],
        ['8'] = ["XXX", "X.X", "XXX", "X.X", "XXX"],
        ['9'] = ["XXX", "X.X", "XXX", "..X", "XXX"],
        ['+'] = ["...", ".X.", "XXX", ".X.", "..."],
    };

    private readonly uint[] _pixels = new uint[ArtWidth * ArtHeight];
    private readonly WriteableBitmap _bitmap = new(ArtWidth, ArtHeight, 96, 96, PixelFormats.Bgra32, null);
    private readonly Image _image;
    private readonly DispatcherTimer _timer = new();
    private string _mood = "";
    private int _waiting = -1, _done = -1, _frame, _offset;

    public PetControl()
    {
        _image = new Image { Source = _bitmap, Width = ArtWidth * Zoom, Height = ArtHeight * Zoom, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Root = _image;
        // Przy skalowaniu ekranu (125%, 150%) piksel grafiki musi mieć całą liczbę pikseli ekranu,
        // inaczej część „pikseli” wychodzi grubsza od reszty.
        _image.Loaded += (_, _) => FitToDpi();
        _image.IsVisibleChanged += (_, _) => UpdateTimer();
        _timer.Tick += (_, _) => { _frame++; Draw(); };
        Set(new Dictionary<string, int>());
    }

    public FrameworkElement Root { get; }

    /// <summary>
    /// Liczby sesji w stanach świateł (czeka / pracuje / gotowe). Postać pokazuje najpilniejszy,
    /// a liczniki obok — ile sesji czeka i ile ma nowy wynik, czyli wszystko, co wymaga akcji.
    /// </summary>
    public void Set(IReadOnlyDictionary<string, int> counts)
    {
        var waiting = counts.GetValueOrDefault("czeka");
        var done = counts.GetValueOrDefault("gotowe");
        var mood = waiting > 0 ? "czeka" : counts.GetValueOrDefault("pracuje") > 0 ? "pracuje" : done > 0 ? "gotowe" : "idle";
        if (mood == _mood && waiting == _waiting && done == _done) return;
        if (mood != _mood) _frame = 0;
        _mood = mood;
        _waiting = waiting;
        _done = done;
        _timer.Interval = Moods[mood].Tick;
        UpdateTimer();
        Draw();
    }

    private void FitToDpi()
    {
        var scale = PresentationSource.FromVisual(_image)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var devicePixels = Math.Max(2, Math.Round(Zoom * scale));
        _image.Width = ArtWidth * devicePixels / scale;
        _image.Height = ArtHeight * devicePixels / scale;
    }

    private void UpdateTimer()
    {
        if (_image.IsVisible) _timer.Start();
        else _timer.Stop();
    }

    private void Draw()
    {
        Array.Clear(_pixels);
        _offset = FigureLeft;
        var color = Moods[_mood].Color;
        var f = _frame;

        // Cień zostaje na ziemi, gdy postać podskakuje.
        Fill(10, 33, 12, 1, Shadow);
        var y = 4 + _mood switch
        {
            "czeka" => Bounce[f % Bounce.Length],
            "gotowe" => f % 8 is 0 or 1 ? -1 : 0,
            _ => 0,
        };

        // Ręce: opuszczone (przy „pracuje” stukają na zmianę), machanie albo obie w górze.
        var wave = _mood == "czeka";
        var cheer = _mood == "gotowe" && f % 8 is 0 or 1;
        var typing = _mood == "pracuje" ? f % 2 : -1;
        if (!cheer) Box(7, y + 21 - (typing == 0 ? 1 : 0), 4, 5, Shell, ShellLight, ShellShade);
        if (!cheer && !wave) Box(21, y + 21 - (typing == 1 ? 1 : 0), 4, 5, Shell, ShellLight, ShellShade);

        Box(11, y + 25, 5, 4, ShellShade, ShellShade, ShellShade);
        Box(16, y + 25, 5, 4, ShellShade, ShellShade, ShellShade);
        Box(10, y + 20, 12, 7, Shell, ShellLight, ShellShade);
        Fill(14, y + 23, 4, 1, Screen);

        // Antena z kulką w kolorze stanu; przy „czeka” mruga jak czerwone światło.
        Fill(15, y + 5, 2, 4, Outline);
        var lamp = _mood == "czeka" && f % 4 >= 2 ? Dim(color) : color;
        Box(14, y + 1, 4, 4, lamp, lamp, lamp);
        Px(15, y + 2, 0xFFFFFFFF);

        Box(7, y + 8, 18, 13, Shell, ShellLight, ShellShade);
        Fill(9, y + 10, 14, 9, Screen);
        DrawFace(9, y + 10, color, f);

        if (wave)
        {
            var up = f % 4 < 2;
            Box(up ? 24 : 25, y + (up ? 11 : 12), 4, 6, Shell, ShellLight, ShellShade);
        }
        if (cheer)
        {
            Box(4, y + 11, 4, 6, Shell, ShellLight, ShellShade);
            Box(24, y + 11, 4, 6, Shell, ShellLight, ShellShade);
        }

        DrawBubble(y, color, f);
        _offset = 0;
        DrawCounters();

        _bitmap.WritePixels(new Int32Rect(0, 0, ArtWidth, ArtHeight), _pixels, ArtWidth * 4, 0);
    }

    // Twarz na ekranie ma kolor stanu — łączy postać ze światłami sygnalizatora.
    private void DrawFace(int x, int y, uint color, int f)
    {
        switch (_mood)
        {
            case "pracuje":
                Pattern(Prompt, x + 2, y + 2, color);
                if (f % 4 < 2) Fill(x + 6, y + 6, 4, 1, color);
                break;
            case "czeka":
                Fill(x + 3, y + 2, 2, 3, color);
                Fill(x + 9, y + 2, 2, 3, color);
                Fill(x + 6, y + 6, 2, 2, color);
                break;
            case "gotowe":
                Pattern(HappyEye, x + 2, y + 2, color);
                Pattern(HappyEye, x + 9, y + 2, color);
                Pattern(Smile, x + 5, y + 5, color);
                break;
            default:
                Fill(x + 2, y + 4, 3, 1, color);
                Fill(x + 9, y + 4, 3, 1, color);
                break;
        }
    }

    // Dymek nad głową po prawej; przy drzemce zamiast dymka ulatują coraz wyżej literki „z”.
    private void DrawBubble(int y, uint color, int f)
    {
        if (_mood == "idle")
        {
            (int X, int Y)[] spots = [(22, y + 5), (25, y + 1), (28, y - 3)];
            for (var i = 0; i <= f % 3; i++) Outlined(Z, spots[i].X, Math.Max(1, spots[i].Y), Sleep);
            return;
        }

        // Dymek 12×10 — znak ma piksel odstępu od obrysu, inaczej zlewa się z nim.
        var top = Math.Max(0, y - 4);
        var inner = (X: 21, Y: top + 1);
        Box(20, top, 12, 10, color, color, color);
        // Ogonek w stronę głowy.
        Fill(22, top + 9, 2, 1, color);
        Px(21, top + 10, Outline);
        Px(22, top + 10, color);
        Px(23, top + 10, Outline);
        Px(22, top + 11, Outline);
        switch (_mood)
        {
            case "czeka":
                Fill(inner.X + 4, inner.Y + 1, 2, 4, Ink);
                Fill(inner.X + 4, inner.Y + 6, 2, 1, Ink);
                break;
            case "gotowe":
                Pattern(Check, inner.X + 2, inner.Y + 2, Ink);
                break;
            case "pracuje":
                for (var i = 0; i <= f % 3; i++) Fill(inner.X + 1 + 3 * i, inner.Y + 3, 2, 2, Ink);
                break;
        }
    }

    // Liczniki w kolejności świateł: czerwony (czeka) u góry, zielony (nowy wynik) pod nim — każdy
    // zawsze w tym samym miejscu. Pomija się tylko ten, który powtarzałby dymek: jedna sesja,
    // która sama zapala postać.
    private void DrawCounters()
    {
        var alone = _waiting + _done == 1 && _mood is "czeka" or "gotowe";
        if (alone) return;
        if (_waiting > 0) Counter(0, _waiting, Moods["czeka"].Color);
        if (_done > 0) Counter(10, _done, Moods["gotowe"].Color);
    }

    private void Counter(int top, int count, uint color)
    {
        Box(0, top, 7, 9, color, color, color);
        Pattern(Digits[count > 9 ? '+' : (char)('0' + count)], 2, top + 2, Ink);
    }

    private void Px(int x, int y, uint color)
    {
        x += _offset;
        if (x >= 0 && x < ArtWidth && y >= 0 && y < ArtHeight) _pixels[y * ArtWidth + x] = color;
    }

    private void Fill(int x, int y, int width, int height, uint color)
    {
        for (var row = y; row < y + height; row++)
            for (var col = x; col < x + width; col++) Px(col, row, color);
    }

    // Prostokąt z obrysem i ściętymi rogami; jaśniejszy pierwszy i ciemniejszy ostatni rząd wnętrza.
    private void Box(int x, int y, int width, int height, uint fill, uint light, uint shade)
    {
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var edgeX = col == 0 || col == width - 1;
                var edgeY = row == 0 || row == height - 1;
                if (edgeX && edgeY) continue;
                var color = edgeX || edgeY ? Outline : row == 1 ? light : row == height - 2 ? shade : fill;
                Px(x + col, y + row, color);
            }
        }
    }

    private void Pattern(string[] rows, int x, int y, uint color)
    {
        for (var row = 0; row < rows.Length; row++)
            for (var col = 0; col < rows[row].Length; col++)
                if (rows[row][col] == 'X') Px(x + col, y + row, color);
    }

    // Wzorek z obrysem dookoła — żeby jasne „z” było widać także na jasnej tapecie.
    private void Outlined(string[] rows, int x, int y, uint color)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                Pattern(rows, x + dx, y + dy, Outline);
        Pattern(rows, x, y, color);
    }

    private static uint Dim(uint color) =>
        0xFF000000 | ((color >> 17 & 0x7F) << 16) | ((color >> 9 & 0x7F) << 8) | (color >> 1 & 0x7F);
}
