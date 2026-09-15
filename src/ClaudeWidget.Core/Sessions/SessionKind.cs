namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// Rodzaje sesji pokazywane w widżecie — więcej niż <see cref="SessionStates"/>, bo "gotowe" dzieli
/// się na "nowe" (nieprzejrzany wynik) i "bezczynna" (przejrzany). Rodzaj "bezczynna" nie ma światła.
/// </summary>
public static class SessionKinds
{
    public const string Waiting = "czeka";
    public const string New = "nowe";
    public const string Working = "pracuje";
    public const string Idle = "bezczynna";
}

/// <summary>Opis rodzaju sesji: światło, które zapala, etykieta, kolor kropki i pozycja w sortowaniu.</summary>
public sealed record SessionKindInfo(string? Light, string Label, string Color, int Rank);

public static class SessionKindCatalog
{
    /// <summary>Światła sygnalizatora, każde z licznikiem sesji. Kolejność = kolejność na karcie.</summary>
    public static readonly IReadOnlyList<string> Lights = ["czeka", "pracuje", "gotowe"];

    public static readonly IReadOnlyDictionary<string, SessionKindInfo> Kinds = new Dictionary<string, SessionKindInfo>
    {
        [SessionKinds.Waiting] = new("czeka", "Czeka na Ciebie", "#FF5A4E", 4),
        [SessionKinds.New] = new("gotowe", "Nowy wynik", "#3DD68C", 3),
        [SessionKinds.Working] = new("pracuje", "Pracuje", "#FFB224", 2),
        [SessionKinds.Idle] = new(null, "Nic nie czeka", "#6E6E6E", 1),
    };
}
