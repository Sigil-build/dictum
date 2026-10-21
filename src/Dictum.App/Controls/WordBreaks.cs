using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Dictum.App.Controls;

/// <summary>
/// Wrapped text that breaks a line inside a word, as a label in a column narrower than one of its words does: "Кольоров|а"
/// beside a wide control in a narrow window, or in a font wider than Segoe UI, such as DejaVu Sans on Linux. The panels
/// that put a label beside something else put it on a line of its own instead.
/// </summary>
internal static class WordBreaks
{
    /// <summary>Whether a shown text in <paramref name="part"/>, as last measured, ends a line between two letters or digits.</summary>
    public static bool AnyIn(Visual? part) =>
        part is not null && part.GetSelfAndVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && Splits(text));

    private static bool Splits(TextBlock text)
    {
        if (text.TextWrapping == TextWrapping.NoWrap || text.Text is not { Length: > 0 } content)
        {
            return false;
        }

        var lines = text.TextLayout.TextLines;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var end = lines[i].FirstTextSourceIndex + lines[i].Length;
            if (end > 0 && end < content.Length && char.IsLetterOrDigit(content[end - 1]) && char.IsLetterOrDigit(content[end]))
            {
                return true;
            }
        }

        return false;
    }
}
