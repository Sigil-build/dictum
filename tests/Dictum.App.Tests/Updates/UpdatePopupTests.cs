using Dictum.App.Updates;

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

    [Theory]
    [InlineData(null, Home)]
    [InlineData("/home/olena/.local/lib/dictum/dictum-gui", null)]
    [InlineData("/home/olena/.local/lib/dictum/dictum-gui", "")]
    public void AnUnknownPlaceDoesNot(string? program, string? home) => Assert.False(UpdatePopup.ShowsOnLinux(null, program, home));
}
