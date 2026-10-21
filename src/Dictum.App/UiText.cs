using System.Globalization;

namespace Dictum.App;

/// <summary>Fills a resource string's placeholders in the UI's culture, and readies text to be shown.</summary>
internal static class UiText
{
    /// <summary>A zero-width space: a place where a line may break, which shows nothing.</summary>
    private const string BreakHere = "\u200B";

    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>
    /// The text with a place to break after each \ and /, so that a path too long for its line wraps after a folder,
    /// never inside a name. Only for text that is read, not copied: the breaks would go into the copy.
    /// </summary>
    public static string BreakablePaths(string text) => text.Replace("\\", "\\" + BreakHere, StringComparison.Ordinal).Replace("/", "/" + BreakHere, StringComparison.Ordinal);
}
