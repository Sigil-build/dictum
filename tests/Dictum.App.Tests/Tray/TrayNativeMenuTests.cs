using Avalonia.Controls;
using Dictum.App.Tray;

namespace Dictum.App.Tests.Tray;

public class TrayNativeMenuTests(AvaloniaFixture fx)
{
    [Fact]
    public Task ItemsBecomeNativeMenuItems() => fx.Ui(() =>
    {
        var menu = TrayNativeMenu.Build(
            [
                TrayMenuItem.Header("Profile"),
                TrayMenuItem.ForProfile("uk", true, "uk, en"),
                TrayMenuItem.ForProfile("en", false, null),
                TrayMenuItem.Separator,
                TrayMenuItem.Action("Quit Dictum", TrayCommand.Quit),
            ],
            _ => { });

        var items = menu.Items;
        Assert.Equal(5, items.Count);
        var header = Assert.IsType<NativeMenuItem>(items[0]);
        Assert.False(header.IsEnabled);
        var uk = Assert.IsType<NativeMenuItem>(items[1]);
        Assert.Equal(("uk (uk, en)", MenuItemToggleType.CheckBox, true), (uk.Header, uk.ToggleType, uk.IsChecked));
        var en = Assert.IsType<NativeMenuItem>(items[2]);
        Assert.Equal(("en", false), (en.Header, en.IsChecked));
        Assert.IsType<NativeMenuItemSeparator>(items[3]);
        var quit = Assert.IsType<NativeMenuItem>(items[4]);
        Assert.Equal((MenuItemToggleType.None, true), (quit.ToggleType, quit.IsEnabled));
        Assert.Null(quit.Command);
    });
}
