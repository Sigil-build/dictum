using Avalonia.Controls;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public partial class PastingView : UserControl
{
    public PastingView()
    {
        InitializeComponent();
        AppBox.TextChanged += (_, _) => ProfilesView.Typed(AppBox, text => (DataContext as PastingPage)?.AddApps(text));
    }
}
