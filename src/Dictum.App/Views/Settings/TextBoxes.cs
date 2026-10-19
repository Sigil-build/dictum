using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Dictum.App.Views.Settings;

/// <summary>Text box behaviour the Settings views share.</summary>
internal static class TextBoxes
{
    private const string ApiKeyClass = "apiKey";

    /// <summary>
    /// Leaving an API key field (class <c>apiKey</c>) commits it, as Enter does: a typed key must not sit unsaved in a
    /// field. <paramref name="commit"/> gives the field's commit command.
    /// </summary>
    public static void CommitKeysOnLeave(Control root, Func<TextBox, ICommand?> commit) =>
        root.AddHandler(InputElement.LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox box && box.Classes.Contains(ApiKeyClass) && commit(box) is { } command)
            {
                command.Execute(null);
            }
        }, RoutingStrategies.Bubble);

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
