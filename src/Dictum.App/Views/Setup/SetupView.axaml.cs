using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Dictum.App.Views.Setup;

public sealed partial class SetupView : UserControl
{
    private const string MakeMainClass = "makeMain";
    private const string PickRowClass = "pickRow";

    public SetupView()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnClick, RoutingStrategies.Bubble);
    }

    /// <summary>"Make main" hides once its language is the main one, so the focus stays in the row, on its box.</summary>
    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button button && button.Classes.Contains(MakeMainClass)
            && button.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(PickRowClass)) is { } row)
        {
            Dispatcher.UIThread.Post(() => row.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault()?.Focus(), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Step 8's try-it box takes the focus when it appears: the daemon pastes into the focused control, so the first
    /// dictation lands in the box instead of on the Finish button.
    /// </summary>
    private void FocusTryBox(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }
    }
}
