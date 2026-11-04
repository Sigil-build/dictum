using System.Globalization;
using System.Text;

namespace Dictum.Core.Text;

/// <summary>
/// Text from a file the user may have copied, such as <c>config.json</c> or a plugin's <c>plugin.json</c>, or from a
/// plugin itself, that Dictum prints: what a terminal must not be given raw.
/// </summary>
public static class TerminalText
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
    /// the BMP; null as nothing. A <c>\</c> is never spelt out, so text shown once is shown the same again.
    /// </summary>
    public static string Show(string? value) => Spell(value, keepLineBreaks: false);

    /// <summary>
    /// Like <see cref="Show"/>, for text of several lines, such as an exception with its stack trace: a <c>\n</c>, and a
    /// <c>\r</c> directly before one, stay as they are. A lone <c>\r</c>, a tab and every other hidden character are
    /// spelt out.
    /// </summary>
    public static string ShowLines(string? value) => Spell(value, keepLineBreaks: true);

    private static string Spell(string? value, bool keepLineBreaks)
    {
        var text = value ?? "";
        var shown = new StringBuilder();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var next = index + rune.Utf16SequenceLength;
            var keep = keepLineBreaks && (rune.Value == '\n' || (rune.Value == '\r' && next < text.Length && text[next] == '\n'));
            shown.Append(keep || !IsHidden(rune) ? rune.ToString() : rune.IsBmp ? $"\\u{rune.Value:x4}" : $"\\U{rune.Value:x8}");
            index = next;
        }

        return shown.ToString();
    }
}
