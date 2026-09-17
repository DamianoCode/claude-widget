namespace ClaudeWidget.Core.Settings;

/// <summary>Na ile wyciszyć dźwięki i powiadomienia.</summary>
public enum MuteDuration { HalfHour, OneHour, UntilTomorrow }

public static class MuteOptions
{
    public static readonly IReadOnlyList<(MuteDuration Duration, string Label)> All =
    [
        (MuteDuration.HalfHour, "Na 30 minut"),
        (MuteDuration.OneHour, "Na 1 godzinę"),
        (MuteDuration.UntilTomorrow, "Do jutra"),
    ];

    /// <summary>Koniec wyciszenia; „do jutra” kończy się o najbliższej północy czasu lokalnego.</summary>
    public static DateTimeOffset Until(MuteDuration duration, DateTimeOffset now) => duration switch
    {
        MuteDuration.HalfHour => now.AddMinutes(30),
        MuteDuration.OneHour => now.AddHours(1),
        _ => new DateTimeOffset(now.Date.AddDays(1), now.Offset),
    };

    /// <summary>Opis stanu do menu, np. „Wyciszone do 14:30”.</summary>
    public static string Describe(DateTimeOffset until, DateTimeOffset now) =>
        until.Date == now.Date
            ? $"Wyciszone do {until:HH\\:mm}"
            : until.TimeOfDay == TimeSpan.Zero && until.Date == now.Date.AddDays(1)
                ? "Wyciszone do jutra"
                : $"Wyciszone do {until:d.MM HH\\:mm}";
}
