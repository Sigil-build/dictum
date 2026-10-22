using Dictum.App.Shell;
using Dictum.App.Tests.Tray;
using Dictum.App.Updates;
using Dictum.Core.Diagnostics;
using Dictum.Platform;

namespace Dictum.App.Tests.Updates;

/// <summary>Which Linux installs get the notification: the ones the user updates by hand.</summary>
public sealed class UpdatePopupTests
{
    private const string Home = "/home/olena";

    [Fact]
    public void AnAppImageGetsIt() =>
        Assert.True(UpdatePopup.ShowsOnLinux("/home/olena/Applications/Dictum.AppImage", "/tmp/.mount_DictumX1/usr/bin/dictum-gui", Home));

    [Theory]
    [InlineData("/home/olena/.local/lib/dictum/dictum-gui")]
    [InlineData("/home/olena/Downloads/dictum-0.4.0-linux-x64/dictum-gui")]
    public void TheTarballGetsIt(string program) => Assert.True(UpdatePopup.ShowsOnLinux(null, program, Home));

    [Fact]
    public void AHomeWithATrailingSlashIsTheSame() =>
        Assert.True(UpdatePopup.ShowsOnLinux(null, "/home/olena/.local/lib/dictum/dictum-gui", Home + "/"));

    [Theory]
    [InlineData("/opt/dictum/dictum-gui")]
    [InlineData("/usr/bin/dictum-gui")]
    [InlineData("/usr/lib/dictum/dictum-gui")]
    [InlineData("/home/olena-old/.local/lib/dictum/dictum-gui")]
    public void APackageManagersInstallDoesNot(string program) => Assert.False(UpdatePopup.ShowsOnLinux(null, program, Home));

    [Fact]
    public void ForNeedsTheDesktop() => Assert.Throws<ArgumentNullException>(() => UpdatePopup.For(null!, new ListLog()));

    [Fact]
    public void NoneShowsNothingAndNeverClicks()
    {
        var clicked = false;

        UpdatePopup.None.Show(null, "Dictum 0.5.0 is out", "Open the release page", () => clicked = true);

        Assert.False(clicked);
    }

    [Fact]
    public void ThisDesktopGetsThePopupItsOsHas()
    {
        var log = new ListLog();

        var popup = UpdatePopup.For(DesktopEnvironment.Detect(), log);

        if (OperatingSystem.IsWindows())
        {
            Assert.NotSame(UpdatePopup.None, popup);
        }
        else if (OperatingSystem.IsLinux())
        {
            var byHand = UpdatePopup.ShowsOnLinux(InstallLocation.AppImage, Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.Equal(byHand, popup is IDisposable);
            Assert.Equal(!byHand, ReferenceEquals(popup, UpdatePopup.None));
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
        var popup = UpdatePopup.For(DesktopEnvironment.Detect(), log);

        popup.Show(null, "Dictum 0.5.0 is out", "Open the release page", () => { });
        popup.Show(new TrayControllerTests.FakePresenter(), "Dictum 0.5.0 is out", "Open the release page", () => { });

        Assert.Equal(2, log.Lines.Count(l => l.Level == LogLevel.Info && l.Message == "No update notification: the tray icon cannot be reached"));
    }

    [Theory]
    [InlineData(null, Home)]
    [InlineData("/home/olena/.local/lib/dictum/dictum-gui", null)]
    [InlineData("/home/olena/.local/lib/dictum/dictum-gui", "")]
    public void AnUnknownPlaceDoesNot(string? program, string? home) => Assert.False(UpdatePopup.ShowsOnLinux(null, program, home));
}
