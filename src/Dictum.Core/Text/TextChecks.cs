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
            if (char.IsAsciiLetter(c) || c is >= '\u00C0' and <= '\u024F')
            {
                latin++;
            }
            else if (c is >= '\u0400' and <= '\u04FF')
            {
                cyrillic++;
            }
        }

        return letters == 0 ? Script.None
            : latin * 5 >= letters * 4 ? Script.Latin
            : cyrillic * 5 >= letters * 4 ? Script.Cyrillic
            : Script.Mixed;
    }

    /// <summary>One side is almost all Latin and the other almost all Cyrillic, i.e. the text was translated.</summary>
    public static bool IsScriptFlip(string input, string output) =>
        (DominantScript(input), DominantScript(output)) is (Script.Latin, Script.Cyrillic) or (Script.Cyrillic, Script.Latin);

    [GeneratedRegex(@"[\uFEFF\u200B-\u200D\u2060]")]
    private static partial Regex Invisible();
}
