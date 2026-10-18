using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Dictum.App.Views.Setup;

public partial class SetupView : UserControl
{
    public SetupView() => InitializeComponent();

    /// <summary>
    /// Step 8's try-it box takes the focus when it appears: the daemon pastes into the focused control, so the first
    /// dictation lands in the box instead of on the Finish button (PR 5b).
    /// </summary>
    private void FocusTryBox(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }
    }
}
