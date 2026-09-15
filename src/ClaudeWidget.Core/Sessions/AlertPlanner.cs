namespace ClaudeWidget.Core.Sessions;

public enum AlertKind { Waiting, NewResult }

/// <summary>Sesja właśnie zaczęła czekać albo skończyła z nowym wynikiem; <see cref="Sound"/> — czy limit pozwala zagrać.</summary>
public sealed record Alert(SessionInfo Session, AlertKind Kind, bool Sound);

/// <summary>Nowe powiadomienia i sesje, których powiadomienie jest już nieaktualne.</summary>
public sealed record AlertChanges(IReadOnlyList<Alert> Raised, IReadOnlyList<string> Cleared)
{
    public static readonly AlertChanges None = new([], []);
}

/// <summary>
/// Z kolejnych odczytów sesji wylicza, o czym powiadomić: sesja weszła w „czeka” albo „nowy wynik”,
/// a których powiadomień już nie trzeba (sesja przestała czekać, wynik przejrzany, sesja zamknięta).
/// Pierwszy odczyt po starcie to stan zastany, nie zmiana — niczego nie ogłasza. Seria próśb
/// o zgodę w jednej sesji gra dźwięk najwyżej raz na 15 s, a powiadomienie się podmienia.
/// </summary>
public sealed class AlertPlanner
{
    public const long SoundThrottleMs = 15_000;

    private readonly Dictionary<(string Id, AlertKind Kind), long> _lastSound = [];
    private Dictionary<string, Entry>? _previous;
    private bool _backgroundKnown;

    private readonly record struct Entry(string Kind, bool Background);

    /// <param name="backgroundKnown">
    /// Czy lista `claude agents` jest dostępna i świeża. Bez niej sesji w tle po prostu nie widać —
    /// to „nie wiadomo”, nie „zamknięte”: ich powiadomienia zostają, a gdy lista wróci (także pierwsza
    /// po starcie widżetu), nie ogłasza się ich od nowa.
    /// </param>
    public AlertChanges Next(IReadOnlyList<SessionInfo> sessions, long nowMs, bool backgroundKnown = true)
    {
        var current = new Dictionary<string, Entry>();
        foreach (var session in sessions) current.TryAdd(session.Id, new Entry(session.Kind, session.IsBackground));
        var previous = _previous;
        if (!backgroundKnown && previous is not null)
        {
            foreach (var (id, entry) in previous)
            {
                if (entry.Background) current.TryAdd(id, entry);
            }
        }
        var backgroundBaseline = backgroundKnown && !_backgroundKnown;
        _previous = current;
        _backgroundKnown = backgroundKnown;
        if (previous is null) return AlertChanges.None;

        foreach (var stale in _lastSound.Where(entry => nowMs - entry.Value >= SoundThrottleMs).Select(entry => entry.Key).ToList())
        {
            _lastSound.Remove(stale);
        }

        var raised = new List<Alert>();
        foreach (var session in sessions)
        {
            if (AlertOf(session.Kind) is not AlertKind kind) continue;
            if (previous.TryGetValue(session.Id, out var before) && before.Kind == session.Kind) continue;
            if (backgroundBaseline && session.IsBackground && !previous.ContainsKey(session.Id)) continue;
            if (raised.Exists(alert => alert.Session.Id == session.Id)) continue;
            raised.Add(new Alert(session, kind, !_lastSound.ContainsKey((session.Id, kind))));
        }

        var cleared = previous
            .Where(entry => AlertOf(entry.Value.Kind) is not null
                && (!current.TryGetValue(entry.Key, out var now) || now.Kind != entry.Value.Kind)
                && !raised.Exists(alert => alert.Session.Id == entry.Key))
            .Select(entry => entry.Key)
            .ToList();
        return new AlertChanges(raised, cleared);
    }

    /// <summary>
    /// Dźwięk faktycznie zagrał — dopiero to zajmuje 15-sekundowy limit. Alarm wyciszony, bo akurat
    /// patrzysz na sesję, nie może uciszyć następnej prośby, której już nie widzisz.
    /// </summary>
    public void MarkSounded(Alert alert, long nowMs) => _lastSound[(alert.Session.Id, alert.Kind)] = nowMs;

    private static AlertKind? AlertOf(string kind) =>
        kind == SessionKinds.Waiting ? AlertKind.Waiting
        : kind == SessionKinds.New ? AlertKind.NewResult
        : null;
}
