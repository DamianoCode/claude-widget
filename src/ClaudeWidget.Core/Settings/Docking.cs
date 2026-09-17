namespace ClaudeWidget.Core.Settings;

/// <summary>Krawędź ekranu, do której przyklejony jest widżet.</summary>
public enum DockEdge { None, Left, Right, Top, Bottom }

/// <summary>Prostokąt w jednostkach niezależnych od DPI (WPF).</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + Width / 2;
}

/// <summary>
/// Gdzie stoi widżet przyklejony do krawędzi. Po bokach jest pionowy, 8 px od krawędzi obszaru
/// roboczego; u góry i u dołu to pozioma „wyspa” przyklejona wprost do krawędzi (u dołu — do paska
/// zadań), którą przesuwa się wzdłuż niej. Okno jest większe od karty o margines na cień.
/// </summary>
public static class Docking
{
    public const double Gap = 8;
    public const double SnapDistance = 24;

    // Windows nie przesuwa okna ponad górną krawędź ekranu, a okno ma nad kartą margines na cień
    // (24 px) — karta nie podejdzie do góry bliżej, więc tu zasięg jest większy.
    public const double TopSnapDistance = 48;

    public static bool IsHorizontal(DockEdge edge) => edge is DockEdge.Top or DockEdge.Bottom;

    public static string ToText(DockEdge edge) => edge switch
    {
        DockEdge.Left => "left",
        DockEdge.Right => "right",
        DockEdge.Top => "top",
        DockEdge.Bottom => "bottom",
        _ => "",
    };

    public static DockEdge Parse(string? text) => text switch
    {
        "left" => DockEdge.Left,
        "right" => DockEdge.Right,
        "top" => DockEdge.Top,
        "bottom" => DockEdge.Bottom,
        _ => DockEdge.None,
    };

    /// <summary>
    /// Krawędź po upuszczeniu karty. Boki wygrywają z górą i dołem: karta w rogu zostaje pionowa
    /// i tylko dosuwa się do rogu (tak było, zanim doszła wyspa), a wyspą staje się dopiero karta
    /// upuszczona przy górze albo dole z dala od boków.
    /// </summary>
    public static DockEdge Decide(Box card, Box area)
    {
        if (Math.Abs(area.Right - card.Right) < SnapDistance) return DockEdge.Right;
        if (Math.Abs(card.X - area.X) < SnapDistance) return DockEdge.Left;
        if (Math.Abs(card.Y - area.Y) < TopSnapDistance) return DockEdge.Top;
        if (Math.Abs(area.Bottom - card.Bottom) < SnapDistance) return DockEdge.Bottom;
        return DockEdge.None;
    }

    /// <summary>
    /// Który ekran (indeks w <paramref name="screens"/>) ma przyklejony widżet po restarcie albo -1,
    /// gdy tego ekranu już nie ma. Liczy się punkt przy krawędzi, a nie cała zapisana pozycja: wyspa
    /// u dołu sięga marginesem cienia poza ekran, a zapisana szerokość mogła być jeszcze zwinięta.
    /// U góry i u dołu punktem jest środek wyspy (<paramref name="anchorX"/>).
    /// </summary>
    public static int SavedScreen(DockEdge edge, double left, double top, double? anchorX, double shadow, IReadOnlyList<Box> screens)
    {
        var x = IsHorizontal(edge) && anchorX is double anchor ? anchor : left + shadow;
        var y = top + shadow;
        for (var i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            if (x >= s.X && x < s.Right && y >= s.Y && y < s.Bottom) return i;
        }
        return -1;
    }

    /// <summary>
    /// Pozycja okna (lewy górny róg) dla karty o rozmiarze <paramref name="card"/>.
    /// Po bokach: pionowo zostaje <paramref name="cardTop"/>, dosunięte do góry albo dołu, gdy jest
    /// blisko, i zawsze w obszarze. U góry i u dołu: środek karty w <paramref name="anchorX"/>
    /// (bez niego — środek obszaru), cała karta w obszarze.
    /// </summary>
    public static (double Left, double Top) Place(DockEdge edge, double cardWidth, double cardHeight, Box area, double shadow,
        double cardLeft, double cardTop, double? anchorX)
    {
        if (IsHorizontal(edge))
        {
            var center = anchorX ?? area.CenterX;
            var left = Math.Clamp(center - cardWidth / 2, area.X, Math.Max(area.X, area.Right - cardWidth));
            var top = edge == DockEdge.Top ? area.Y : area.Bottom - cardHeight;
            return (left - shadow, top - shadow);
        }

        var x = edge switch
        {
            DockEdge.Right => area.Right - Gap - cardWidth,
            DockEdge.Left => area.X + Gap,
            _ => cardLeft,
        };
        var y = cardTop;
        if (edge != DockEdge.None)
        {
            if (Math.Abs(area.Bottom - (y + cardHeight)) < SnapDistance) y = area.Bottom - Gap - cardHeight;
            else if (Math.Abs(y - area.Y) < TopSnapDistance) y = area.Y + Gap;
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - cardHeight));
        }
        return (x - shadow, y - shadow);
    }
}
