using Dictum.App.Tray;
using Dictum.Platform.Linux.Tray;

namespace Dictum.App.Tests.Tray;

public class SniMenuTests
{
    private static readonly TrayMenuItem[] Items =
    [
        TrayMenuItem.Header("Profile"),
        TrayMenuItem.ForProfile("dictate_uk", selected: true, "uk, en"),
        TrayMenuItem.ForProfile("en", selected: false, "auto"),
        TrayMenuItem.Separator,
        TrayMenuItem.Action("Settings", TrayCommand.Settings),
    ];

    [Fact]
    public void ItemsBecomeDbusmenuNodes()
    {
        var (root, _) = SniMenu.Build(Items, generation: 1, rightAlignDetail: false);

        var nodes = root.Children!;
        Assert.Equal(5, nodes.Count);
        Assert.False(nodes[0].Enabled);
        Assert.Equal((MenuNode.Checkmark, 1), (nodes[1].ToggleType, nodes[1].ToggleState));
        Assert.Equal((MenuNode.Checkmark, 0), (nodes[2].ToggleType, nodes[2].ToggleState));
        Assert.True(nodes[3].Separator);
        Assert.Equal(("", -1), (nodes[4].ToggleType, nodes[4].ToggleState));
    }

    [Fact]
    public void UnderscoresAreNotTakenForAccessKeys()
    {
        var (root, _) = SniMenu.Build(Items, 1, rightAlignDetail: false);

        Assert.Equal("dictate__uk (uk, en)", root.Children![1].Label);
    }

    [Fact]
    public void PlasmaGetsTheLanguagesRightAligned()
    {
        var (root, _) = SniMenu.Build(Items, 1, rightAlignDetail: true);

        Assert.Equal("dictate__uk\tuk, en", root.Children![1].Label);
    }

    [Fact]
    public void EveryBuildUsesFreshIds()
    {
        var (first, _) = SniMenu.Build(Items, 1, false);
        var (second, _) = SniMenu.Build(Items, 2, false);

        var ids = first.Children!.Select(n => n.Id).Concat(second.Children!.Select(n => n.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(0, ids);
    }

    [Fact]
    public void OnlyCommandsCanBeClicked()
    {
        var (root, commands) = SniMenu.Build(Items, 1, false);

        Assert.Equal(3, commands.Count);
        Assert.Equal(TrayCommand.Settings, commands[root.Children![4].Id].Command);
        Assert.False(commands.ContainsKey(root.Children[0].Id));
    }
}
