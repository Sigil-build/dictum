using Dictum.Platform.Linux;
using Dictum.Platform.Linux.X11;
using Dictum.Testing;

namespace Dictum.Core.Tests.Linux;

/// <summary>Parsing only: runs on every OS.</summary>
public class OverlayDesktopSettingsTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Xft.antialias:\t1\nXft.dpi:\t120\nXft.hinting:\t1\n", 120.0)]
    [InlineData("Xft.dpi: 96", 96.0)]
    [InlineData("Xft.dpi:\tabc\n", null)]
    [InlineData("Xcursor.size:\t24\n", null)]
    [InlineData(null, null)]
    public void XftDpiIsReadFromTheResourceDatabase(string? database, double? dpi) => Assert.Equal(dpi, XResources.Dpi(database));

    [Theory]
    [InlineData(120.0, ":0", null, "1.25")]
    [InlineData(144.0, ":0", null, "1.5")]
    [InlineData(96.0, ":0", null, null)]
    [InlineData(null, ":0", null, null)]
    [InlineData(120.0, null, null, null)]
    [InlineData(120.0, ":0", "2", null)]
    public void TheScaleIsPassedOnOnlyWhenNobodySetOne(double? dpi, string? display, string? userScale, string? expected)
    {
        var environment = new Dictionary<string, string?> { ["DISPLAY"] = display, [DisplayScale.Variable] = userScale };

        Assert.Equal(expected, DisplayScale.FactorFor(name => environment.GetValueOrDefault(name), dpi));
    }

    [Theory]
    [InlineData("[General]\nAnimationDurationFactor=0\n[KDE]\nAnimationDurationFactor=0\n", true)]
    [InlineData("[KDE]\nSingleClick=false\nAnimationDurationFactor=0.5\n", false)]
    [InlineData("[General]\nAnimationDurationFactor=0\n", false)]
    [InlineData("[KDE]\r\nAnimationDurationFactor=0\r\n", true)]
    [InlineData(null, false)]
    public void KdeReducesMotionWhenAnimationsAreInstant(string? kdeglobals, bool reduced) =>
        Assert.Equal(reduced, DesktopMotion.KdeReduced(kdeglobals));

    [Theory]
    [InlineData("false\n", true)]
    [InlineData("true\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void GnomeReducesMotionWhenAnimationsAreOff(string? output, bool reduced) =>
        Assert.Equal(reduced, DesktopMotion.GnomeReduced(output));

    [Fact]
    public async Task KdeSettingsAreReadFromTheConfigHome()
    {
        using var config = new TempDirectory();
        File.WriteAllText(config.File("kdeglobals"), "[KDE]\nAnimationDurationFactor=0\n");
        var kde = new SessionInfo(SessionType.Wayland, Desktop.Kde, "KDE");

        Assert.True(await DesktopMotion.ReducedAsync(kde, name => name == "XDG_CONFIG_HOME" ? config.Path : null, _ct));
        Assert.False(await DesktopMotion.ReducedAsync(kde with { Desktop = Desktop.Other }, _ => null, _ct));
    }
}
