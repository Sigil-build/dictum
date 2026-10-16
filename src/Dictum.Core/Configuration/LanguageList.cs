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

    /// <summary>Languages Whisper often confuses with each other; with two of them in one list it may pick the wrong one.</summary>
    private static readonly string[][] SimilarGroups =
    [
        ["uk", "be", "ru"], ["pl", "cs", "sk"], ["es", "pt", "gl"], ["no", "nn", "da", "sv"], ["hr", "sr", "bs"], ["id", "ms"], ["hi", "ur"],
    ];

    /// <summary>Pairs from <paramref name="codes"/> that sound alike to Whisper, in list order.</summary>
    public static IReadOnlyList<(string First, string Second)> SimilarPairs(IReadOnlyList<string> codes)
    {
        var pairs = new List<(string First, string Second)>();
        for (var i = 0; i < codes.Count; i++)
        {
            for (var j = i + 1; j < codes.Count; j++)
            {
                if (SimilarGroups.Any(g => g.Contains(codes[i]) && g.Contains(codes[j])))
                {
                    pairs.Add((codes[i], codes[j]));
                }
            }
        }

        return pairs;
    }

    /// <summary>One sentence for the user, or null when nothing in the list sounds alike.</summary>
    public static string? SimilarWarning(IReadOnlyList<string> codes)
    {
        var pairs = SimilarPairs(codes);
        return pairs.Count == 0
            ? null
            : $"{string.Join(", ", pairs.Select(p => $"'{p.First}' and '{p.Second}'"))} sound alike to Whisper; with both in the list it may pick the wrong one. Keep only the languages you dictate in.";
    }

    private static bool IsCode(string code) => code.Length is 2 or 3 && code.All(char.IsAsciiLetterLower);
}
