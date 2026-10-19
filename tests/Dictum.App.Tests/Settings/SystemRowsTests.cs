using System.ComponentModel;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class SystemRowsTests
{
    [Fact]
    public void AHiddenWindowsIconOffersTheTaskbarSettings()
    {
        var opened = 0;

        var row = TrayVisibility.Row(TrayDesktop.Windows, available: true, hidden: true, () => opened++);

        Assert.Equal((HealthLevel.Warning, Strings.SettingsTrayHidden, Strings.SettingsTrayHiddenWindows), (row.Level, row.Status, row.Detail));
        Assert.Equal(Strings.SettingsTrayOpen, row.FixLabel);
        row.Fix!.Execute(null);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void ATaskbarSettingsPageThatCannotOpenIsLoggedNotThrown()
    {
        var log = new ListLog();

        TrayVisibility.OpenTaskbarSettings(log, _ => throw new Win32Exception(1155, "No application is associated with ms-settings:"));

        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("No application is associated", StringComparison.Ordinal));
    }

    [Fact]
    public void AHiddenKdeIconSaysWhereTheSettingIs()
    {
        var row = TrayVisibility.Row(TrayDesktop.Kde, available: true, hidden: true, openSettings: null);

        Assert.Equal((HealthLevel.Warning, Strings.SettingsTrayHiddenKde), (row.Level, row.Detail));
        Assert.Null(row.Fix);
    }

    [Theory]
    [InlineData(false, "SettingsTrayShown")]
    [InlineData(null, "SettingsTrayInTray")]
    public void AVisibleOrUnknownIconIsFine(bool? hidden, string status)
    {
        var row = TrayVisibility.Row(TrayDesktop.Windows, available: true, hidden, openSettings: null);

        Assert.Equal((HealthLevel.Ok, Strings.ResourceManager.GetString(status, Strings.Culture)), (row.Level, row.Status));
        Assert.Equal(Strings.SettingsTrayName, row.Name);
    }

    [Fact]
    public void WithoutATrayHostTheRowSaysSo()
    {
        var row = TrayVisibility.Row(TrayDesktop.Other, available: false, hidden: null, openSettings: null);

        Assert.Equal((HealthLevel.Warning, Strings.SettingsTrayNoHost, Strings.StatusNoTray), (row.Level, row.Status, row.Detail));
    }

    [Theory]
    [InlineData(OverlayHost.Windows, true, HealthLevel.Ok, "SettingsOverlayRowReady")]
    [InlineData(OverlayHost.XWayland, true, HealthLevel.Ok, "SettingsOverlayRowReady")]
    [InlineData(OverlayHost.NoX, true, HealthLevel.Error, "SettingsOverlayRowNoX")]
    [InlineData(OverlayHost.Unsupported, true, HealthLevel.Warning, "SettingsOverlayRowUnsupported")]
    [InlineData(OverlayHost.NoX, false, HealthLevel.Ok, "SettingsOverlayRowOff")]
    public void TheOverlayRowFollowsTheDesktopAndTheSetting(OverlayHost host, bool enabled, HealthLevel level, string status)
    {
        var row = OverlayAvailability.Row(host, enabled);

        Assert.Equal((level, Strings.ResourceManager.GetString(status, Strings.Culture)), (row.Level, row.Status));
        Assert.Equal(Strings.SettingsOverlay, row.Name);
    }

    [Theory]
    [InlineData(":0", null, OverlayHost.X11)]
    [InlineData(":0", "wayland-0", OverlayHost.XWayland)]
    [InlineData(null, "wayland-0", OverlayHost.NoX)]
    public void LinuxNeedsAnXDisplay(string? display, string? wayland, OverlayHost expected) =>
        Assert.Equal(expected, OverlayAvailability.ThisDesktop(windows: false, linux: true, name => name switch
        {
            "DISPLAY" => display,
            "WAYLAND_DISPLAY" => wayland,
            _ => null,
        }));

    [Fact]
    public void WindowsAlwaysHasTheOverlayAndOtherSystemsNone()
    {
        Assert.Equal(OverlayHost.Windows, OverlayAvailability.ThisDesktop(windows: true, linux: false, _ => null));
        Assert.Equal(OverlayHost.Unsupported, OverlayAvailability.ThisDesktop(windows: false, linux: false, _ => ":0"));
    }
}
