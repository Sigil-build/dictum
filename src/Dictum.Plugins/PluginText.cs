using System.Globalization;
using System.Text;

namespace Dictum.Plugins;

/// <summary>Text from a plugin's folder or its config that Dictum prints: what a terminal must not be given raw.</summary>
internal static class PluginText
{
    /// <summary>
    /// A control character, or one a terminal draws as nothing or as a line break: a format character, such as the
    /// right-to-left override U+202E, a zero-width space or a tag character, or the line and paragraph separators U+2028
    /// and U+2029. Each can move the cursor, reorder a line or split it.
    /// </summary>
    public static bool IsHidden(Rune rune) =>
        Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>Whether the text holds a hidden character; found by rune, so a format character outside the BMP counts.</summary>
    public static bool HasHidden(string? value) => value is not null && value.EnumerateRunes().Any(IsHidden);

    /// <summary>
    /// The value for a message, with each hidden character spelt out as <c>\uXXXX</c>, or as <c>\UXXXXXXXX</c> outside
    /// the BMP; null as nothing.
    /// </summary>
    public static string Show(string? value)
    {
        var shown = new StringBuilder();
        foreach (var rune in (value ?? "").EnumerateRunes())
        {
            shown.Append(!IsHidden(rune) ? rune.ToString() : rune.IsBmp ? $"\\u{rune.Value:x4}" : $"\\U{rune.Value:x8}");
        }

        return shown.ToString();
    }
}
