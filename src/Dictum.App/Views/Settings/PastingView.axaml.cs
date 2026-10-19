using Avalonia.Controls;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public sealed partial class PastingView : UserControl
{
    public PastingView()
    {
        InitializeComponent();
        AppBox.TextChanged += (_, _) => TextBoxes.Typed(AppBox, text => (DataContext as PastingPage)?.AddApps(text));
    }
}
