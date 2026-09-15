using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ClaudeWidget;

/// <summary>Pasek limitu: etykieta, wartość procentowa, wypełnienie i opis pod spodem. Port New-Meter/Set-Meter.</summary>
public sealed class MeterControl
{
    public StackPanel Root { get; }
    private readonly TextBlock _value;
    private readonly Border _fill;
    private readonly TextBlock _note;
    private readonly double _width;

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
    }
}
