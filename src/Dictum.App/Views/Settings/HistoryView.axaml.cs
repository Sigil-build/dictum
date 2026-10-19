using Avalonia.Controls;

namespace Dictum.App.Views.Settings;

public sealed partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        Pickers.ClearAfterPick(this);
    }
}
