namespace ClaudeWidget.Core.Settings;

public static class PetSkins
{
    public static readonly (string Key, string Label)[] All =
    [
        ("classic", "Robot"),
        ("portfolio", "Bluza i sneakersy"),
        ("christmas", "Świąteczny"),
        ("pixel", "Pikselowy (pierwsza wersja)"),
    ];

    public static string Normalize(string? key) => key is "portfolio" or "christmas" or "pixel" ? key : "classic";
}
