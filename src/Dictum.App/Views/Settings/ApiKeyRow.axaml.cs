using Avalonia.Controls;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

/// <summary>The API key row of an LLM processor and of a remote speech provider.</summary>
public sealed partial class ApiKeyRow : UserControl
{
    public ApiKeyRow()
    {
        InitializeComponent();

        // Leaving the field commits it, as Enter does: a typed key must not sit unsaved in a field.
        KeyBox.LostFocus += (_, _) => (DataContext as ApiKeyField)?.CommitCommand.Execute(null);
    }
}
