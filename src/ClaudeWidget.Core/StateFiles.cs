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

    /// <summary>PID procesu Claude Code, rodzica hooka.</summary>
    public int? Pid { get; init; }

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

/// <summary><c>limits.json</c> — limity konta z ostatniego odczytu statusline dowolnej sesji.</summary>
public sealed record AccountLimits
{
    public LimitWindow? FiveHour { get; init; }

    public LimitWindow? SevenDay { get; init; }

    public long UpdatedAt { get; init; }
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
[JsonSerializable(typeof(SeenMarker))]
public sealed partial class StateJson : JsonSerializerContext;
