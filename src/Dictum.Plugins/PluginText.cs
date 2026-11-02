using System.Globalization;

namespace Dictum.Plugins;

/// <summary>Text from a plugin's folder or its config that Dictum prints: what a terminal must not be given raw.</summary>
internal static class PluginText
{
    /// <summary>
    /// A control character, or one a terminal draws as nothing or as a line break: a format character, such as the
    /// right-to-left override U+202E or a zero-width space, or the line and paragraph separators U+2028 and U+2029.
    /// Each can move the cursor, reorder a line or split it.
    /// </summary>
    public static bool IsHidden(char c) =>
        char.IsControl(c)
        || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>The value for a message, with each hidden character spelt out as <c>\uXXXX</c>; null as nothing.</summary>
    public static string Show(string? value) =>
        string.Concat((value ?? "").Select(c => IsHidden(c) ? $"\\u{(int)c:x4}" : c.ToString()));
}
