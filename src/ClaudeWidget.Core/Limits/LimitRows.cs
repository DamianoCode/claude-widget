using ClaudeWidget.Core.Text;

namespace ClaudeWidget.Core.Limits;

/// <summary>
/// Jeden pasek limitu z etykietami dla każdego miejsca: karta, panel i wyspa.
/// <see cref="IsAccountWide"/> — limit całego konta (5 h, tydzień); tylko on ostrzega obramowaniem,
/// bo wyczerpany limit jednego modelu nie zatrzymuje pracy na pozostałych.
/// </summary>
public sealed record LimitRow(string Key, string CardLabel, string PanelLabel, string IslandLabel, LimitView View, bool IsAccountWide);

/// <summary>
/// Które limity pokazać i w jakiej kolejności. Wartości 5 h i tygodnia biorą się z <c>limits.json</c>
/// (statusline albo serwer — co świeższe), a o tym, które limity w ogóle obowiązują, i o limitach
/// pojedynczych modeli mówi pełny odczyt serwera.
/// </summary>
public static class LimitRows
{
    public const string FiveHourKey = "five_hour";
    public const string SevenDayKey = "seven_day";

    public static IReadOnlyList<LimitRow> Build(AccountLimits? limits, ServerLimits? server, long nowMs, bool panel)
    {
        // Bez czasu pomiaru (0 z pliku bez updatedAt) nie ma ani świeżości, ani prognozy tempa.
        long? measuredAt = limits is { UpdatedAt: > 0 } ? limits.UpdatedAt : null;
        var rows = new List<LimitRow>();

        // Okno 5 h znika tylko wtedy, gdy serwer mówi, że plan go nie ma (np. Max 20x), a statusline
        // też go nie podała. Bez odczytu serwera zostaje jak dotąd — z kreską, póki nie ma danych.
        var showFiveHour = server is null || server.HasFiveHour || limits?.FiveHour is not null;
        if (showFiveHour)
        {
            rows.Add(new LimitRow(FiveHourKey, "Limit 5 h", "Limit 5 h", "5 h",
                LimitCalculator.GetView(limits?.FiveHour, LimitWindowKind.FiveHour, measuredAt, nowMs, panel), IsAccountWide: true));
        }
        rows.Add(new LimitRow(SevenDayKey, "Tydzień", "Limit tygodniowy", "tydz.",
            LimitCalculator.GetView(limits?.SevenDay, LimitWindowKind.SevenDay, measuredAt, nowMs, panel), IsAccountWide: true));

        long? fetchedAt = server is { FetchedAt: > 0 } ? server.FetchedAt : null;
        // Limity modeli zna tylko serwer. Gdy jego odczyt się zestarzeje (np. 429, wygasły token),
        // pasek mówi, z kiedy jest — stopka karty dotyczy świeższego limits.json.
        var stale = fetchedAt is long fetched && nowMs - fetched >= UsagePollPolicy.ServerInterval.TotalMilliseconds
            ? Formatting.GetFreshnessText(fetched)
            : null;
        foreach (var scoped in server?.Scoped ?? [])
        {
            if (string.IsNullOrWhiteSpace(scoped.Label)) continue;
            var window = scoped.Group switch
            {
                "weekly" => LimitWindowKind.SevenDay,
                "session" => LimitWindowKind.FiveHour,
                _ => (LimitWindowKind?)null,
            };
            var panelLabel = window switch
            {
                LimitWindowKind.SevenDay => $"{scoped.Label} · tydzień",
                LimitWindowKind.FiveHour => $"{scoped.Label} · 5 h",
                _ => scoped.Label,
            };
            var view = LimitCalculator.GetView(new LimitWindow { Pct = scoped.Pct, ResetsAt = scoped.ResetsAt }, window, fetchedAt, nowMs, panel);
            if (stale is not null && view.Pct is not null)
            {
                view = view with { Note = panel && view.Note.Length > 0 ? $"{stale} · {view.Note}" : stale };
            }
            rows.Add(new LimitRow($"model:{scoped.Label}:{scoped.Group}", scoped.Label, panelLabel, scoped.Label, view, IsAccountWide: false));
        }
        return rows;
    }

    /// <summary>Kolor obramowania: najpoważniejsze ostrzeżenie spośród limitów całego konta.</summary>
    public static string? Warning(IEnumerable<LimitRow> rows)
    {
        var warnings = rows.Where(row => row.IsAccountWide).Select(row => row.View.Warn).ToList();
        return warnings.Contains("#FF5A4E") ? "#FF5A4E" : warnings.FirstOrDefault(warn => warn is not null);
    }
}
