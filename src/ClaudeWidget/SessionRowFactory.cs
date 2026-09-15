using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeWidget.Core.Sessions;

namespace ClaudeWidget;

/// <summary>Buduje wiersz sesji w panelu: kropka, nazwa, opis, podgląd wyniku, pasek kontekstu. Port New-SessionRow.</summary>
public static class SessionRowFactory
{
    public static Border Create(SessionInfo session, long nowMs, Style rowStyle, Style waitingRowStyle, Action<SessionInfo> onClick)
    {
        var row = new Border
        {
            Style = session.Kind == SessionKinds.Waiting ? waitingRowStyle : rowStyle,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 2),
            Tag = session,
            ToolTip = session.Kind == SessionKinds.New && session.Summary.Length > 0
                ? $"{session.Summary}\n\nKliknij, aby przejść do terminala tej sesji."
                : session.Kind == SessionKinds.Waiting
                    ? $"{SessionFormatting.GetDetail(session, nowMs)}\n\nKliknij, aby przejść do terminala tej sesji."
                    : "Kliknij, aby przejść do terminala tej sesji.",
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick(session);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 10, 0),
            Fill = Brushes.Brush(SessionKindCatalog.Kinds[session.Kind].Color),
        };
        if (session.Kind != SessionKinds.Idle)
        {
            dot.Effect = Brushes.Glow(Brushes.Color(SessionKindCatalog.Kinds[session.Kind].Color), 10, 0.8);
        }

        var texts = new StackPanel();
        var title = new TextBlock
        {
            Text = session.Name,
            FontSize = 13,
            Foreground = Brushes.Brush(session.Kind == SessionKinds.Idle ? "#BDBDBD" : "#F3F3F3"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var meta = new TextBlock
        {
            Text = SessionFormatting.GetSessionMeta(session, nowMs),
            FontSize = 12,
            Foreground = Brushes.Brush("#A6A6A6"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };
        texts.Children.Add(title);
        texts.Children.Add(meta);
        // Podgląd nowego wyniku: pierwsze zdanie odpowiedzi, żeby ocenić, czy przełączać się od razu.
        if (session.Kind == SessionKinds.New && session.Summary.Length > 0)
        {
            texts.Children.Add(new TextBlock
            {
                Text = session.Summary,
                FontSize = 11.5,
                Foreground = Brushes.Brush("#8A8A8A"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        Grid.SetColumn(texts, 1);

        var usage = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(usage, 2);
        if (session.ContextPct is double contextPct)
        {
            var pct = Math.Max(0, Math.Min(100, contextPct));
            // Kontekst blisko pełnego zapowiada automatyczne streszczanie rozmowy.
            var valueColor = pct >= 80 ? "#FFB224" : "#C8C8C8";
            var value = new TextBlock { Text = $"{pct:0}%", FontSize = 12, Foreground = Brushes.Brush(valueColor), HorizontalAlignment = HorizontalAlignment.Right };
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            var track = new Border { Width = 48, Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 0, 0), Background = Brushes.Brush("#14FFFFFF") };
            var fill = new Border { Height = 3, Width = 48 * pct / 100, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Brush(pct >= 80 ? "#FFB224" : "#BDBDBD") };
            track.Child = fill;
            usage.Children.Add(value);
            usage.Children.Add(track);
        }

        grid.Children.Add(dot);
        grid.Children.Add(texts);
        grid.Children.Add(usage);
        row.Child = grid;
        return row;
    }
}
