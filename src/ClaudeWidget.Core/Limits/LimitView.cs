namespace ClaudeWidget.Core.Limits;

/// <summary>Widok jednego okna limitu konta: procent, kolor, opis i próg ostrzeżenia (dla obramowania).</summary>
public sealed record LimitView
{
    public double? Pct { get; init; }

    public string Color { get; init; } = "#D97757";

    public string Note { get; init; } = "";

    /// <summary>"#FFB224" albo "#FF5A4E", gdy limit zasługuje na ostrzeżenie; inaczej null.</summary>
    public string? Warn { get; init; }
}
