using Avalonia.Controls;
using Avalonia.Interactivity;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public partial class SpeechProvidersView : UserControl
{
    public SpeechProvidersView()
    {
        InitializeComponent();

        // Leaving a key field commits it, as Enter does: a typed key must not sit unsaved in a field.
        AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox box && box.Classes.Contains("apiKey") && box.DataContext is ProviderCard card)
            {
                card.CommitKeyCommand.Execute(null);
            }
        }, RoutingStrategies.Bubble);
    }
}
