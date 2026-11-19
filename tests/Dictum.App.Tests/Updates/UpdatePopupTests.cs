using Dictum.App.Shell;
using Dictum.App.Tests.Tray;
using Dictum.App.Updates;
using Dictum.Core.Diagnostics;
using Dictum.Updates;

namespace Dictum.App.Tests.Updates;

/// <summary>Which Linux installs get the notification: all but the ones a package manager updates.</summary>
public sealed class UpdatePopupTests
{
    [Theory]
    [InlineData(InstallKind.AppImage)]
    [InlineData(InstallKind.Tarball)]
    [InlineData(InstallKind.Portable)]
    public void WhatTheUserUpdatesGetsIt(InstallKind kind) => Assert.True(UpdatePopup.ShowsOnLinux(kind));

    [Fact]
    public void ThePackageManagerGetsNoNotificationOnLinux() => Assert.False(UpdatePopup.ShowsOnLinux(InstallKind.PackageManager));

    [Fact]
    public void ForNeedsTheDesktop() => Assert.Throws<ArgumentNullException>(() => UpdatePopup.For(null!, Installation.Portable, new ListLog()));

    [Fact]
    public void NoneShowsNothingAndNeverClicks()
    {
        var clicked = false;

        UpdatePopup.None.Show(null, "Dictum 0.5.0 is out", "Open the release page", () => clicked = true);

        Assert.False(clicked);
    }

    [Fact]
    public void APackageManagersInstallGetsNoneOnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The desktop notification is Linux only");

        Assert.Same(UpdatePopup.None, UpdatePopup.For(DesktopEnvironment.Detect(), new Installation(InstallKind.PackageManager, "/opt/dictum"), new ListLog()));
    }

    [Fact]
    public void ThisDesktopGetsThePopupItsOsHas()
    {
        var popup = UpdatePopup.For(DesktopEnvironment.Detect(), Installation.Portable, new ListLog());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.NotSame(UpdatePopup.None, popup);
            (popup as IDisposable)?.Dispose();
        }
        else
        {
            Assert.Same(UpdatePopup.None, popup);
        }
    }

    [Fact]
    public void WindowsLogsWhenTheTrayIconCannotBeReached()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The tray balloon is Windows only");
        var log = new ListLog();
        var popup = UpdatePopup.For(DesktopEnvironment.Detect(), Installation.Portable, log);

        popup.Show(null, "Dictum 0.5.0 is out", "Open the release page", () => { });
        popup.Show(new TrayControllerTests.FakePresenter(), "Dictum 0.5.0 is out", "Open the release page", () => { });

        Assert.Equal(2, log.Lines.Count(l => l.Level == LogLevel.Info && l.Message == "No update notification: the tray icon cannot be reached"));
    }
}
