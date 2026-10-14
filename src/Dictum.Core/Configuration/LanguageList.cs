namespace Dictum.Core.Configuration;

/// <summary>
/// A profile's <c>language</c> setting: one ISO 639-1 code, <c>auto</c>, or a comma-separated list of codes the
/// recognizer picks from on each dictation, e.g. <c>"uk, en"</c>.
/// </summary>
public static class LanguageList
{
    /// <returns>
    /// No codes for <c>auto</c> (or nothing), one for a fixed language, several to pick from; null when the setting is
    /// not valid (an unknown form, a repeated code, or <c>auto</c> inside a list).
    /// </returns>
    public static IReadOnlyList<string>? Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting) || setting.Trim() == "auto")
        {
            return [];
        }

        var codes = setting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return codes.Length > 0 && codes.All(IsCode) && codes.Distinct(StringComparer.Ordinal).Count() == codes.Length ? codes : null;
    }

    /// <summary>The setting as Dictum writes it: <c>auto</c>, <c>uk</c> or <c>uk, en</c>.</summary>
    public static string Format(IReadOnlyList<string> codes) => codes.Count == 0 ? "auto" : string.Join(", ", codes);

    private static bool IsCode(string code) => code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower);
}
