using Avalonia;
using Dictum.App.Overlay;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Overlay;

public class OverlayPlacementTests
{
    // The user's two monitors from the KDE spike.
    private static readonly ScreenArea Primary = new("DP-6", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1394), 1, true);
    private static readonly ScreenArea Second = new("DP-2", new PixelRect(2560, 0, 2560, 1440), new PixelRect(2560, 0, 2560, 1394), 1.25, false);

    [Fact]
    public void TheOverlaySitsCentredFourPixelsAboveThePanel() =>
        Assert.Equal(new PixelRect(1040, 1294, 480, 96), OverlayPlacement.Place(Primary, OverlaySize.Medium, OverlayPosition.Bottom));

    [Fact]
    public void TheTopPositionSitsBelowATopPanel()
    {
        var screen = Primary with { WorkingArea = new PixelRect(0, 30, 2560, 1410) };

        Assert.Equal(34, OverlayPlacement.Place(screen, OverlaySize.Medium, OverlayPosition.Top).Y);
    }

    [Fact]
    public void SizeAndDisplayScaleMultiply()
    {
        var rect = OverlayPlacement.Place(Second, OverlaySize.Large, OverlayPosition.Bottom);

        Assert.Equal(750, rect.Width);
        Assert.Equal(150, rect.Height);
        Assert.Equal(2560 + (2560 - 750) / 2, rect.X);
        Assert.Equal(1394 - 150 - 5, rect.Y);
    }

    // Review focus 4. Measured in the spike: DP-6 at 125 % made XWayland cut DP-2's working area to DP-6's height.
    [Fact]
    public void AWorkingAreaCutByAnotherScreenIsIgnored()
    {
        var cut = new ScreenArea("DP-2", new PixelRect(2048, 0, 2560, 1440), new PixelRect(2048, 0, 2560, 1106), 1, false);

        Assert.Equal(1440 - 48 - 96 - 4, OverlayPlacement.Place(cut, OverlaySize.Medium, OverlayPosition.Bottom).Y);
    }

    [Fact]
    public void AWorkingAreaOutsideItsScreenIsIgnored()
    {
        var odd = Primary with { WorkingArea = new PixelRect(0, 0, 5120, 1440) };

        Assert.Equal(1440 - 96 - 4, OverlayPlacement.Place(odd, OverlaySize.Medium, OverlayPosition.Bottom).Y);
    }

    [Fact]
    public void TheNamedScreenWinsThenThePointThenThePrimary()
    {
        IReadOnlyList<ScreenArea> screens = [Second, Primary];

        Assert.Same(Second, OverlayPlacement.Choose(screens, new ScreenHint(null, "dp-2")));
        Assert.Same(Second, OverlayPlacement.Choose(screens, new ScreenHint(new PixelPoint(3000, 500), null)));
        Assert.Same(Primary, OverlayPlacement.Choose(screens, new ScreenHint(new PixelPoint(-50, 500), "HDMI-1")));
        Assert.Same(Primary, OverlayPlacement.Choose(screens, ScreenHint.None));
        Assert.Null(OverlayPlacement.Choose([], ScreenHint.None));
    }
}
