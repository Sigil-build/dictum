using Avalonia.Controls;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public sealed partial class SpeechProvidersView : UserControl
{
    public SpeechProvidersView()
    {
        InitializeComponent();
        TextBoxes.CommitKeysOnLeave(this, box => (box.DataContext as ProviderCard)?.CommitKeyCommand);
    }
}
