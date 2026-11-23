using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWidget.Core;

/// <summary>Wartości pola <c>state</c> w pliku stanu sesji — po polsku, jak w plikach z wersji z Node.</summary>
public static class SessionStates
{
    /// <summary>Claude prosi o zgodę, zadaje pytanie albo tura skończyła się błędem API.</summary>
    public const string Waiting = "czeka";

    /// <summary>Claude wykonuje zadanie, także gdy tura się skończyła, a praca trwa w tle.</summary>
    public const string Working = "pracuje";

    /// <summary>Odpowiedź skończona; <see cref="SessionState.Fresh"/> mówi, czy to nowy wynik.</summary>
    public const string Done = "gotowe";

    public static bool IsKnown(string? state) => state is Waiting or Working or Done;
}

/// <summary><c>&lt;sesja&gt;.state.json</c> — pisze go wyłącznie hook.</summary>
public sealed record SessionState
{
    public required string State { get; init; }

    /// <summary>Od kiedy sesja jest w tym stanie (ms od epoki).</summary>
    public long Since { get; init; }

    /// <summary>Przy <c>gotowe</c>: true — nowy wynik do przejrzenia, false — sesja po prostu stoi.</summary>
    public bool? Fresh { get; init; }

    public long? TurnStartedAt { get; init; }

    public string? Detail { get; init; }

    /// <summary>Pierwsze zdanie ostatniej odpowiedzi — podgląd nowego wyniku.</summary>
    public string? Summary { get; init; }

    /// <summary>Liczba zadań w tle, gdy tura się skończyła, a praca trwa.</summary>
    public int? Background { get; init; }

    /// <summary>
    /// Narzędzie, o które była prośba o zgodę: obiekt <c>{ name, input }</c>, a w plikach sprzed
    /// tej wersji — napis „nazwa\0argumenty”.
    /// </summary>
    public JsonElement? PendingTool { get; init; }

    public string? Cwd { get; init; }

    /// <summary>
    /// Transkrypt sesji (JSONL Claude Code). Po nim widżet rozpoznaje turę przerwaną klawiszem Esc,
    /// po której nie przychodzi żadne zdarzenie hooka (<see cref="Sessions.InterruptedTurn"/>).
    /// </summary>
    public string? TranscriptPath { get; init; }

    /// <summary>PID procesu Claude Code, rodzica hooka.</summary>
    public int? Pid { get; init; }

    /// <summary>Identyfikator agenta Orca (<c>ORCA_AGENT_SESSION_ID</c>); tylko w sesjach uruchomionych przez Orca.</summary>
    public string? OrcaSessionId { get; init; }

    /// <summary>Uchwyt terminala Orca (<c>ORCA_TERMINAL_HANDLE</c>); tylko w sesjach w terminalu Orca.</summary>
    public string? OrcaTerminal { get; init; }

    /// <summary>Czas ostatniego zapisu (ms od epoki). Proces, który wystartował później, nie jest tą sesją.</summary>
    public long UpdatedAt { get; init; }
}

/// <summary><c>&lt;sesja&gt;.usage.json</c> — pisze go wyłącznie statusline.</summary>
public sealed record SessionUsage
{
    public string? Name { get; init; }

    public string? Project { get; init; }

    public string? Model { get; init; }

    public double? ContextPct { get; init; }

    public double? ContextTokens { get; init; }

    public double? ContextSize { get; init; }

    public long UpdatedAt { get; init; }
}

/// <summary>Jedno okno limitu konta: procent zużycia i reset w sekundach od epoki.</summary>
public sealed record LimitWindow
{
    public double? Pct { get; init; }

    public double? ResetsAt { get; init; }
}

/// <summary>
/// <c>limits.json</c> — limity konta z ostatniego odczytu. Jedyny plik z dwoma piszącymi: statusline
/// dowolnej sesji i widżet (endpoint limitów, gdy statusline nie działa). Oba zapisują pełny odczyt
/// konta, a widżet nie nadpisuje odczytu nowszego niż swoje zapytanie.
/// </summary>
public sealed record AccountLimits
{
    public LimitWindow? FiveHour { get; init; }

    public LimitWindow? SevenDay { get; init; }

    public long UpdatedAt { get; init; }

    /// <summary>
    /// Kto zapisał odczyt: <see cref="LimitSources.StatusLine"/> albo <see cref="LimitSources.Server"/>.
    /// Brak — nieznane (np. statusline ze starszej wersji widżetu).
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>
/// <c>limits-server.json</c> — pisze go wyłącznie widżet: to, czego statusline nie zna. Które
/// limity w ogóle obowiązują na tym planie (np. Max 20x nie ma okna 5 h) i limity jednego modelu
/// (np. tygodniowy limit Fable). Osobny plik, bo statusline nadpisuje <c>limits.json</c> w całości.
/// </summary>
public sealed record ServerLimits
{
    /// <summary>Plan ma okno 5 h (serwer podał je w odczycie).</summary>
    public bool HasFiveHour { get; init; }

    /// <summary>Limity jednego modelu — osobne paski obok ogólnych.</summary>
    public List<ScopedLimit>? Scoped { get; init; }

    public long FetchedAt { get; init; }
}

/// <summary>Limit jednego modelu: nazwa, zużycie 0–100 i reset w sekundach od epoki.</summary>
public sealed record ScopedLimit
{
    /// <summary>Nazwa modelu, jak podaje serwer (np. „Fable”).</summary>
    public string Label { get; init; } = "";

    /// <summary>Okno limitu wg serwera: <c>weekly</c>, <c>session</c> albo inne (bez prognozy tempa).</summary>
    public string? Group { get; init; }

    public double? Pct { get; init; }

    public double? ResetsAt { get; init; }
}

/// <summary>Źródła odczytu limitów w <c>limits.json</c>.</summary>
public static class LimitSources
{
    public const string StatusLine = "statusline";
    public const string Server = "server";
}

/// <summary><c>&lt;sesja&gt;.seen.json</c> — pisze go wyłącznie widżet, gdy przejrzałeś wynik.</summary>
public sealed record SeenMarker
{
    public long SeenAt { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionState))]
[JsonSerializable(typeof(SessionUsage))]
[JsonSerializable(typeof(AccountLimits))]
[JsonSerializable(typeof(ServerLimits))]
[JsonSerializable(typeof(SeenMarker))]
public sealed partial class StateJson : JsonSerializerContext;
