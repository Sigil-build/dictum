using Avalonia.Controls;

namespace Dictum.App.Views.Settings;

public sealed partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();
        Pickers.ClearAfterPick(this);
    }
}
