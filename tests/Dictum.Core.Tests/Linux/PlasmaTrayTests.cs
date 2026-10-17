using Dictum.Platform.Linux;

namespace Dictum.Core.Tests.Linux;

public sealed class PlasmaTrayTests
{
    private const string Panel = """
        [Containments][2]
        formfactor=2
        plugin=org.kde.panel

        [Containments][2][Applets][7]
        plugin=org.kde.plasma.systemtray

        [Containments][2][Applets][7][Configuration]
        SystrayContainmentId=8

        [Containments][8]
        formfactor=2
        plugin=org.kde.plasma.private.systemtray

        """;

    [Fact]
    public void AnItemInHiddenItemsIsHidden() =>
        Assert.True(PlasmaTray.IsHidden(Panel + "[Containments][8][General]\nhiddenItems=chrome_status_icon_1,dictum\n", PlasmaTray.ItemId));

    [Fact]
    public void AnItemNotInHiddenItemsIsShown() =>
        Assert.False(PlasmaTray.IsHidden(Panel + "[Containments][8][General]\nhiddenItems=chrome_status_icon_1,dictumx\nshownItems=dictum\n", PlasmaTray.ItemId));

    [Fact]
    public void ShowAllItemsWins() =>
        Assert.False(PlasmaTray.IsHidden(Panel + "[Containments][8][General]\nhiddenItems=dictum\nshowAllItems=true\n", PlasmaTray.ItemId));

    [Fact]
    public void ATrayWithoutSettingsShowsItems() => Assert.False(PlasmaTray.IsHidden(Panel, PlasmaTray.ItemId));

    [Fact]
    public void WithoutASystemTrayItIsUnknown() =>
        Assert.Null(PlasmaTray.IsHidden("[Containments][2]\nplugin=org.kde.panel\n", PlasmaTray.ItemId));

    [Fact]
    public void TheFileIsInTheConfigFolder()
    {
        Assert.Equal(Path.Combine("/home/u", ".config", "plasma-org.kde.plasma.desktop-appletsrc"),
            PlasmaTray.ConfigFile(name => name == "HOME" ? "/home/u" : null));
        Assert.Equal(Path.Combine("/cfg", "plasma-org.kde.plasma.desktop-appletsrc"),
            PlasmaTray.ConfigFile(name => name == "XDG_CONFIG_HOME" ? "/cfg" : "/home/u"));
    }
}
