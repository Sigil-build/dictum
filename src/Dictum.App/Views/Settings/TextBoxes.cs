using Avalonia.Controls;

namespace Dictum.App.Views.Settings;

/// <summary>Text box behaviour the Settings views share.</summary>
internal static class TextBoxes
{
    /// <summary>Puts the part still being typed back into <paramref name="box"/> after complete entries were taken.</summary>
    public static void Typed(TextBox box, Func<string, string?> take)
    {
        if (box.Text is not { Length: > 0 } text || take(text) is not { } rest || rest == text)
        {
            return;
        }

        box.Text = rest;
        box.CaretIndex = rest.Length;
    }
}
