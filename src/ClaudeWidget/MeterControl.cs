using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ClaudeWidget;

/// <summary>
/// Pasek limitu: etykieta, wartość procentowa, wypełnienie i opis pod spodem. Port New-Meter/Set-Meter.
/// Wersja zwarta (wyspa) to jeden wiersz „etykieta — pasek — wartość”, a opis trafia do podpowiedzi.
/// </summary>
public sealed class MeterControl
{
    public StackPanel Root { get; }
    private readonly TextBlock _value;
    private readonly Border _fill;
    private readonly TextBlock _note;
    private readonly double _width;

    private readonly bool _compact;

    public MeterControl(string label, double width, double labelSize, string labelColor, double bottomMargin)
    {
        _width = width;
        Root = new StackPanel { Margin = new Thickness(0, 0, 0, bottomMargin) };

        var head = new Grid();
        _value = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.Brush("#F3F3F3"),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        head.Children.Add(new TextBlock { Text = label, FontSize = labelSize, Foreground = Brushes.Brush(labelColor) });
        head.Children.Add(_value);

        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brushes.Brush("#14FFFFFF"), Margin = new Thickness(0, 4, 0, 0) };
        _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Child = _fill;

        _note = new TextBlock { FontSize = 10.5, Foreground = Brushes.Brush("#767676"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

        Root.Children.Add(head);
        Root.Children.Add(track);
        Root.Children.Add(_note);
    }

    /// <param name="trackWidth">Szerokość samego paska; etykieta i wartość mają stałe kolumny.</param>
    public MeterControl(string label, double trackWidth)
    {
        _compact = true;
        _width = trackWidth;
        Root = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        Root.Children.Add(new TextBlock
        {
            Text = label,
            Width = 36,
            FontSize = 10.5,
            Foreground = Brushes.Brush("#8A8A8A"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var track = new Border
        {
            Width = trackWidth,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Brushes.Brush("#14FFFFFF"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Child = _fill;
        Root.Children.Add(track);
        _value = new TextBlock
        {
            Width = 34,
            FontSize = 10.5,
            Foreground = Brushes.Brush("#E0E0E0"),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
        Root.Children.Add(_value);
        _note = new TextBlock();
    }

    public void Set(double? pct, string note, string color)
    {
        if (pct is null)
        {
            _value.Text = "—";
            _fill.Width = 0;
        }
        else
        {
            var clamped = Math.Max(0, Math.Min(100, pct.Value));
            _value.Text = $"{clamped:0}%";
            _fill.Width = _width * clamped / 100;
            _fill.Background = Brushes.Brush(color);
        }
        _note.Text = note;
        if (_compact) Root.ToolTip = string.IsNullOrEmpty(note) ? null : note;
    }
}
