using Avalonia.Controls;

namespace Dictum.App.Tray;

/// <summary>The tray menu as Avalonia native menu items. Clicks go through <c>Click</c>, never <c>Command</c>.</summary>
public static class TrayNativeMenu
{
    public static NativeMenu Build(IReadOnlyList<TrayMenuItem> items, Action<TrayMenuItem> clicked)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            menu.Add(item.Kind switch
            {
                TrayMenuItemKind.Separator => new NativeMenuItemSeparator(),
                TrayMenuItemKind.Header => new NativeMenuItem(TrayLabels.Escape(item.Label)) { IsEnabled = false },
                _ => Command(item, clicked),
            });
        }

        return menu;
    }

    private static NativeMenuItem Command(TrayMenuItem item, Action<TrayMenuItem> clicked)
    {
        var entry = new NativeMenuItem(TrayLabels.ForItem(item));
        if (item.Command == TrayCommand.UseProfile)
        {
            entry.ToggleType = MenuItemToggleType.CheckBox;
            entry.IsChecked = item.Checked;
        }

        entry.Click += (_, _) => clicked(item);
        return entry;
    }
}
