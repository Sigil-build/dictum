using Dictum.Platform.Linux.Tray;

namespace Dictum.App.Tray;

/// <summary>
/// The tray menu as a dbusmenu tree. Every build uses fresh ids, because Plasma never re-reads an item's type or
/// toggle for an id it already knows (research 5).
/// </summary>
public static class SniMenu
{
    /// <param name="generation">1, 2, …: one per build.</param>
    /// <param name="rightAlignDetail">Plasma: a tab puts the languages in a right-aligned grey column.</param>
    public static (MenuNode Root, IReadOnlyDictionary<int, TrayMenuItem> Commands) Build(
        IReadOnlyList<TrayMenuItem> items, int generation, bool rightAlignDetail)
    {
        var commands = new Dictionary<int, TrayMenuItem>();
        var nodes = new List<MenuNode>();
        var id = generation * 1000;
        foreach (var item in items)
        {
            id++;
            nodes.Add(item.Kind switch
            {
                TrayMenuItemKind.Separator => new MenuNode(id, Separator: true),
                TrayMenuItemKind.Header => new MenuNode(id, TrayLabels.Escape(item.Label), Enabled: false),
                _ when item.Command == TrayCommand.UseProfile =>
                    new MenuNode(id, Label(item, rightAlignDetail), ToggleType: "checkmark", ToggleState: item.Checked ? 1 : 0),
                _ => new MenuNode(id, Label(item, rightAlignDetail)),
            });
            if (item.Kind == TrayMenuItemKind.Command)
            {
                commands[id] = item;
            }
        }

        return (new MenuNode(0, Children: nodes), commands);
    }

    public static string Label(TrayMenuItem item, bool rightAlignDetail) =>
        rightAlignDetail && item.Detail is { } detail
            ? $"{TrayLabels.Escape(item.Label)}\t{TrayLabels.Escape(detail)}"
            : TrayLabels.ForItem(item);
}
