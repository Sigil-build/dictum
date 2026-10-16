namespace Dictum.Core.Configuration;

/// <summary>
/// A profile's <c>language</c> setting: one ISO 639-1 code, <c>auto</c>, or a comma-separated list of codes the
/// recognizer picks from on each dictation, e.g. <c>"uk, en"</c>.
/// </summary>
public static class LanguageList
{
    /// <returns>
    /// No codes for <c>auto</c> (or no setting), one for a fixed language, several to pick from; null when the setting is
    /// not valid. A single code and <c>auto</c> are written exactly; only list entries may have spaces around them, and a
    /// list has no empty entries, no repeats and no <c>auto</c>.
    /// </returns>
    public static IReadOnlyList<string>? Parse(string? setting)
    {
        if (setting is null or "auto")
        {
            return [];
        }

        if (!setting.Contains(','))
        {
            return IsCode(setting) ? [setting] : null;
        }

        var codes = setting.Split(',', StringSplitOptions.TrimEntries);
        return codes.All(IsCode) && codes.Distinct(StringComparer.Ordinal).Count() == codes.Length ? codes : null;
    }

    /// <summary>The setting as Dictum writes it: <c>auto</c>, <c>uk</c> or <c>uk, en</c>.</summary>
    public static string Format(IReadOnlyList<string> codes) => codes.Count == 0 ? "auto" : string.Join(", ", codes);

    private static bool IsCode(string code) => code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower);
}
