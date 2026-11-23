using System.Text.Json.Serialization;

namespace ClaudeWidget.Core.Sessions;

/// <summary>
/// <c>context-windows.json</c> — rozmiary okna kontekstu według modelu, podpatrzone ze statusline
/// (tylko ona je podaje). Jeden model może mieć kilka okien (np. wariant <c>[1m]</c>), a transkrypt
/// podaje samą nazwę bazową — dlatego lista. Pisze go statusline dowolnej sesji.
/// </summary>
public sealed record ContextWindowMap
{
    public Dictionary<string, List<double>> Models { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ContextWindowMap))]
public sealed partial class ContextWindowJson : JsonSerializerContext;

/// <summary>Rozmiar okna kontekstu dla modelu z transkryptu i procent zajętości.</summary>
public static class ContextWindows
{
    public const double Standard = 200_000;
    public const double Large = 1_000_000;

    /// <summary>
    /// Domyślne okna modeli wg dokumentacji Claude (prefiks id → okno). Modele od Opus/Sonnet 4.6
    /// mają domyślnie 1 mln bez żadnego dopisku — bez tej tabeli sesja Opus 5.5 ze 125 tys. tokenów
    /// wyglądała na 62% zajętą (okno 200 tys.), choć Claude Code liczy ją od 1 mln.
    /// Okno podpatrzone ze statusline ma pierwszeństwo.
    /// </summary>
    private static readonly (string Prefix, double Size)[] Defaults =
    [
        ("claude-fable-5", Large),
        ("claude-mythos-5", Large),
        ("claude-opus-5", Large),
        ("claude-opus-4-8", Large),
        ("claude-opus-4-7", Large),
        ("claude-opus-4-6", Large),
        ("claude-sonnet-5", Large),
        ("claude-sonnet-4-6", Large),
        ("claude-haiku-5", Large),
        ("claude-haiku-4-5", Standard),
    ];

    /// <summary>Domyślne okno modelu z tabeli albo null dla modelu spoza niej.</summary>
    public static double? Default(string? model) =>
        Key(model) is { } key && Defaults.FirstOrDefault(entry => key.StartsWith(entry.Prefix, StringComparison.Ordinal)) is { Prefix: not null } match
            ? match.Size
            : null;

    /// <summary>Tyle różnych okien jednego modelu pamiętamy.</summary>
    private const int MaxVariants = 4;

    /// <summary>Klucz modelu: id bez dopisku w nawiasie (np. „[1m]”), małymi literami.</summary>
    public static string? Key(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var bracket = model.IndexOf('[');
        var key = (bracket >= 0 ? model[..bracket] : model).Trim().ToLowerInvariant();
        return key.Length == 0 ? null : key;
    }

    /// <summary>
    /// Rozmiar okna i czy jest pewny. Pewny, gdy statusline podała dla tego modelu jedno okno albo
    /// model jest w tabeli domyślnych — i rozmowa się w nim mieści. Przy kilku podpatrzonych oknach
    /// (warianty modelu) bierzemy najmniejsze, które mieści rozmowę — to szacunek. Model nieznany:
    /// 200 tys., a dla większej rozmowy 1 mln — też szacunek.
    /// </summary>
    public static (double Size, bool Estimated) Resolve(string? model, double tokens, ContextWindowMap? known)
    {
        var sizes = Key(model) is { } key && known?.Models.TryGetValue(key, out var list) == true
            ? list.Where(size => size > 0 && double.IsFinite(size)).Distinct().Order().ToList()
            : [];
        if (sizes.Count == 0 && Default(model) is double builtIn) sizes = [builtIn];
        if (sizes.Count == 0) return (tokens <= Standard ? Standard : Large, true);
        if (sizes.Count == 1 && tokens <= sizes[0]) return (sizes[0], false);
        var fitting = sizes.FirstOrDefault(size => size >= tokens);
        return fitting > 0 ? (fitting, true) : (Math.Max(sizes[^1], Large), true);
    }

    /// <summary>Mapa z dopisanym oknem modelu albo null, gdy nic się nie zmienia (bez zapisu).</summary>
    public static ContextWindowMap? With(ContextWindowMap? current, string? model, double? size)
    {
        if (Key(model) is not { } key || size is not double value || !double.IsFinite(value) || value <= 0) return null;
        var known = current?.Models.GetValueOrDefault(key) ?? [];
        if (known.Contains(value)) return null;
        var models = (current?.Models ?? []).ToDictionary(entry => entry.Key, entry => entry.Value.ToList());
        // Najnowsze okno zostaje; najstarsze odpada, gdy wariantów jest za dużo.
        models[key] = known.Append(value).TakeLast(MaxVariants).ToList();
        return new ContextWindowMap { Models = models };
    }

    /// <summary>
    /// Dopisuje okno modelu do pliku. Kilka statusline może pisać naraz, więc odczyt i zapis są pod
    /// wspólną blokadą; bez niej jedna nadpisałaby model dopisany przez drugą. To tylko podpowiedź
    /// dla innych sesji: blokada zajęta za długo albo błąd zapisu niczego nie zatrzymują.
    /// </summary>
    public static void Learn(WidgetPaths paths, string? model, double? size)
    {
        if (Key(model) is null || size is not double value || !double.IsFinite(value) || value <= 0) return;
        try
        {
            using var mutex = new Mutex(false, MutexName(paths));
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned) return;
            try
            {
                var next = With(JsonStore.Read(paths.ContextWindowsFile, ContextWindowJson.Default.ContextWindowMap), model, size);
                if (next is not null) JsonStore.Write(paths.ContextWindowsFile, next, ContextWindowJson.Default.ContextWindowMap);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Plik zablokowany albo bez uprawnień — następne odświeżenie statusline spróbuje znowu.
        }
    }

    private static string MutexName(WidgetPaths paths) =>
        "Local\\ClaudeWidget-ContextWindows-" + new string(paths.StateDir.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}
