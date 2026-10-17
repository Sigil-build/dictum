using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Dictum.App.Views.Settings;

/// <summary>
/// Dropdowns that act like commands (class <c>picker</c>): once the pick is taken, the dropdown clears itself. The
/// view does it, posted, because a model's reset inside the dropdown's own binding write never reaches it (4b C1).
/// </summary>
internal static class Pickers
{
    public static void ClearAfterPick(Control root) => root.AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
    {
        if (e.Source is ComboBox { SelectedItem: not null } picker && picker.Classes.Contains("picker"))
        {
            Dispatcher.UIThread.Post(() => picker.SelectedItem = null);
        }
    }, RoutingStrategies.Bubble);
}
