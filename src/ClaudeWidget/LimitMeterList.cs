using System.Windows.Controls;
using ClaudeWidget.Core.Limits;

namespace ClaudeWidget;

/// <summary>
/// Paski limitów w jednym miejscu (karta, panel, wyspa). Zestaw limitów zależy od planu i odczytu
/// serwera, więc paski buduje się od nowa tylko, gdy zmieni się ten zestaw — co sekundę zmieniają się
/// jedynie wartości.
/// </summary>
public sealed class LimitMeterList(Panel host, Func<LimitRow, string> label, Func<string, MeterControl> create)
{
    private readonly List<MeterControl> _meters = [];
    private string? _signature;

    public void Update(IEnumerable<LimitRow> rows, LimitDisplayMode mode)
    {
        var list = rows.ToList();
        var signature = string.Join('\n', list.Select(row => $"{row.Key}|{label(row)}"));
        if (signature != _signature)
        {
            _signature = signature;
            host.Children.Clear();
            _meters.Clear();
            foreach (var row in list)
            {
                var meter = create(label(row));
                _meters.Add(meter);
                host.Children.Add(meter.Root);
            }
        }
        var word = LimitDisplay.Word(mode);
        for (var i = 0; i < list.Count; i++)
        {
            var view = list[i].View;
            _meters[i].Set(LimitDisplay.Shown(view.Pct, mode), view.Note, view.Color, word);
        }
    }
}
