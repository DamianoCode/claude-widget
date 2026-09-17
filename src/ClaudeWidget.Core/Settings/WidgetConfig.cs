using System.Text.Json.Serialization;

namespace ClaudeWidget.Core.Settings;

/// <summary>
/// widget-config.json: pozycja, krawędź i rozmiar okna, dźwięki, powiadomienia i pozostałe ustawienia.
/// Pisze go widżet (menu, okno ustawień), ale da się go też poprawić ręcznie — widżet wczytuje
/// zmiany na bieżąco. Brak wpisu = wartość domyślna; wartości spoza zakresu przycina się przy odczycie.
/// </summary>
public sealed record WidgetConfig
{
    public const int DefaultVolume = 100;
    public const int DefaultSoundRepeatSeconds = 15;
    public const string DefaultHotkey = "Ctrl+Alt+K";
    public const int MinOpacity = 30;

    public double? Left { get; init; }

    public double? Top { get; init; }

    public string? Size { get; init; }

    /// <summary>Krawędź, do której przyklejony jest widżet: left, right, top, bottom; brak = swobodnie.</summary>
    public string? Dock { get; init; }

    /// <summary>Środek wyspy wzdłuż górnej albo dolnej krawędzi (jednostki WPF).</summary>
    public double? DockAnchor { get; init; }

    /// <summary>Dźwięk, gdy sesja czeka albo ma nowy wynik; brak wpisu = włączone.</summary>
    public bool? Sounds { get; init; }

    /// <summary>Powiadomienie Windows w tych samych chwilach; brak wpisu = włączone.</summary>
    public bool? Notifications { get; init; }

    /// <summary>Dźwięk „czeka”: <c>builtin</c>, <c>none</c> albo ścieżka do pliku WAV/MP3 (<see cref="AlertSounds"/>).</summary>
    public string? SoundWaiting { get; init; }

    /// <summary>Dźwięk „nowy wynik” — jak <see cref="SoundWaiting"/>.</summary>
    public string? SoundDone { get; init; }

    /// <summary>Głośność dźwięków w procentach (0–100).</summary>
    public int? Volume { get; init; }

    /// <summary>Do kiedy (ms od epoki) dźwięki i powiadomienia milczą.</summary>
    public long? MutedUntil { get; init; }

    /// <summary>Najwyżej jeden dźwięk na tyle sekund dla jednej sesji i rodzaju; 0 = bez limitu.</summary>
    public int? SoundRepeatSeconds { get; init; }

    /// <summary>Globalny skrót do sesji, która czeka najdłużej, np. <c>Ctrl+Alt+K</c>; pusty = wyłączony.</summary>
    public string? Hotkey { get; init; }

    /// <summary>Chowanie widżetu, gdy na jego monitorze działa coś na pełnym ekranie; brak wpisu = włączone.</summary>
    public bool? HideOnFullscreen { get; init; }

    /// <summary>Krycie karty widżetu w procentach (30–100); pod kursorem zawsze pełne.</summary>
    public int? Opacity { get; init; }

    [JsonIgnore] public DockEdge DockEdge => Docking.Parse(Dock);

    [JsonIgnore] public bool SoundsOn => Sounds ?? true;

    [JsonIgnore] public bool NotificationsOn => Notifications ?? true;

    [JsonIgnore] public int VolumePercent => Math.Clamp(Volume ?? DefaultVolume, 0, 100);

    [JsonIgnore] public long SoundRepeatMs => Math.Clamp(SoundRepeatSeconds ?? DefaultSoundRepeatSeconds, 0, 3600) * 1000L;

    [JsonIgnore] public string HotkeyText => Hotkey ?? DefaultHotkey;

    [JsonIgnore] public bool HidesOnFullscreen => HideOnFullscreen ?? true;

    [JsonIgnore] public int OpacityPercent => Math.Clamp(Opacity ?? 100, MinOpacity, 100);

    public bool IsMuted(long nowMs) => MutedUntil is long until && until > nowMs;

    /// <summary>Te same ustawienia bez położenia i rozmiaru okna — do porównania po ręcznej zmianie pliku.</summary>
    public WidgetConfig WithoutPlacement() => this with { Left = null, Top = null, Size = null, Dock = null, DockAnchor = null };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WidgetConfig))]
public sealed partial class WidgetConfigJson : JsonSerializerContext;

public static class WidgetConfigStore
{
    /// <summary>Ustawienia albo null, gdy pliku nie ma, jest zajęty albo nie jest poprawnym JSON-em.</summary>
    public static WidgetConfig? Read(string path) => JsonStore.Read(path, WidgetConfigJson.Default.WidgetConfig);

    public static void Write(string path, WidgetConfig config) => JsonStore.Write(path, config, WidgetConfigJson.Default.WidgetConfig);
}
