using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudeWidget;

/// <summary>Pędzle i poświaty z pamięcią podręczną po kolorze — port Get-Brush/Get-Color/New-Glow z widget.ps1.</summary>
public static class Brushes
{
    private static readonly Dictionary<string, SolidColorBrush> BrushCache = [];

    public static SolidColorBrush Brush(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached)) return cached;
        var brush = new SolidColorBrush(Color(hex));
        brush.Freeze();
        BrushCache[hex] = brush;
        return brush;
    }

    // Nazwa "Color" jako typ zwracany niżej wskazywałaby na tę metodę, nie na System.Windows.Media.Color
    // (metoda przesłania typ o tej samej nazwie w swoim zasięgu) — stąd pełna nazwa w sygnaturach.
    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    public static Color Mix(Color color, Color with, double amount) => System.Windows.Media.Color.FromRgb(
        (byte)(color.R + (with.R - color.R) * amount),
        (byte)(color.G + (with.G - color.G) * amount),
        (byte)(color.B + (with.B - color.B) * amount));

    public static DropShadowEffect Glow(Color color, double blur, double opacity)
    {
        var effect = new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
        effect.Freeze();
        return effect;
    }

    // Zapalone światło: jaśniejszy odblask u góry, pełny kolor w środku, ciemniejszy brzeg.
    public static RadialGradientBrush LitFill(Color on)
    {
        var gradient = new RadialGradientBrush
        {
            GradientOrigin = new System.Windows.Point(0.35, 0.3),
            Center = new System.Windows.Point(0.42, 0.4),
            RadiusX = 0.7,
            RadiusY = 0.7,
        };
        gradient.GradientStops.Add(new GradientStop(Mix(on, Colors.White, 0.6), 0.0));
        gradient.GradientStops.Add(new GradientStop(on, 0.5));
        gradient.GradientStops.Add(new GradientStop(Mix(on, Colors.Black, 0.2), 1.0));
        gradient.Freeze();
        return gradient;
    }
}
