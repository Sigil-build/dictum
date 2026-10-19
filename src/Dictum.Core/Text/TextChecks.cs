using System.Text.RegularExpressions;

namespace Dictum.Core.Text;

public enum Script
{
    /// <summary>No letters at all.</summary>
    None,
    Latin,
    Cyrillic,

    /// <summary>Neither script covers 80 % of the letters.</summary>
    Mixed,
}

/// <summary>Cheap text measurements used to accept or reject a processor's answer.</summary>
public static partial class TextChecks
{
    /// <summary>Latin-1 Supplement through Latin Extended-B: accented Latin letters.</summary>
    private const char LatinAccentedFirst = 'À';
    private const char LatinAccentedLast = 'ɏ';

    /// <summary>The basic Cyrillic block.</summary>
    private const char CyrillicFirst = 'Ѐ';
    private const char CyrillicLast = 'ӿ';

    /// <summary>Removes the byte-order mark and zero-width characters that models emit and <c>Trim()</c> keeps.</summary>
    public static string StripInvisible(string text) => Invisible().Replace(text, string.Empty);

    public static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static bool HasLineBreak(string text) => text.AsSpan().IndexOfAny('\r', '\n') >= 0;

    /// <summary>The script of at least 80 % of the letters.</summary>
    public static Script DominantScript(string text)
    {
        int letters = 0, latin = 0, cyrillic = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            if (char.IsAsciiLetter(c) || c is >= LatinAccentedFirst and <= LatinAccentedLast)
            {
                latin++;
            }
            else if (c is >= CyrillicFirst and <= CyrillicLast)
            {
                cyrillic++;
            }
        }

        return letters == 0 ? Script.None
            : IsDominant(latin, letters) ? Script.Latin
            : IsDominant(cyrillic, letters) ? Script.Cyrillic
            : Script.Mixed;
    }

    /// <summary>At least 80 % of the letters, in integers.</summary>
    private static bool IsDominant(int count, int letters) => count * 5 >= letters * 4;

    /// <summary>One side is almost all Latin and the other almost all Cyrillic, i.e. the text was translated.</summary>
    public static bool IsScriptFlip(string input, string output) =>
        (DominantScript(input), DominantScript(output)) is (Script.Latin, Script.Cyrillic) or (Script.Cyrillic, Script.Latin);

    [GeneratedRegex(@"[\uFEFF\u200B-\u200D\u2060]")]
    private static partial Regex Invisible();
}
