using System.Text.Json.Serialization;

namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// <c>context-windows.json</c> — rozmiar okna kontekstu według modelu, podpatrzony ze statusline
/// (tylko ona go podaje). Dzięki temu sesje bez statusline (Orca, IDE) dostają prawdziwy procent.
/// Pisze go statusline dowolnej sesji; każdy zapis to pełna mapa.
/// </summary>
public sealed record ContextWindowMap
{
    public Dictionary<string, double> Models { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ContextWindowMap))]
public sealed partial class ContextWindowJson : JsonSerializerContext;

/// <summary>Rozmiar okna kontekstu dla modelu z transkryptu i procent zajętości.</summary>
public static class ContextWindows
{
    public const double Standard = 200_000;
    public const double Large = 1_000_000;

    /// <summary>Klucz modelu: id bez dopisku w nawiasie (np. „[1m]”), małymi literami.</summary>
    public static string? Key(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var bracket = model.IndexOf('[');
        var key = (bracket >= 0 ? model[..bracket] : model).Trim().ToLowerInvariant();
        return key.Length == 0 ? null : key;
    }

    /// <summary>
    /// Rozmiar okna: podpatrzony ze statusline albo — gdy model jest nieznany — szacowany: 200 tys.,
    /// a jeśli rozmowa jest już większa, 1 mln. Kontekst większy od znanego okna też podnosi je do 1 mln.
    /// </summary>
    public static (double Size, bool Estimated) Resolve(string? model, double tokens, ContextWindowMap? known)
    {
        if (Key(model) is { } key && known?.Models.TryGetValue(key, out var size) == true && size > 0 && double.IsFinite(size))
        {
            return tokens <= size ? (size, false) : (Math.Max(size, Large), true);
        }
        return (tokens <= Standard ? Standard : Large, true);
    }

    /// <summary>Mapa z dopisanym oknem modelu albo null, gdy nic się nie zmienia (bez zapisu).</summary>
    public static ContextWindowMap? With(ContextWindowMap? current, string? model, double? size)
    {
        if (Key(model) is not { } key || size is not double value || !double.IsFinite(value) || value <= 0) return null;
        if (current?.Models.TryGetValue(key, out var existing) == true && existing == value) return null;
        var models = new Dictionary<string, double>(current?.Models ?? []) { [key] = value };
        return new ContextWindowMap { Models = models };
    }
}
