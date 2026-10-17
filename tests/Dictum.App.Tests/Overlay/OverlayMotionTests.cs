using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

public class OverlayMotionTests
{
    [Fact]
    public void DoneContractsThenTheCheckDrawsThenItFades()
    {
        Assert.Equal(220, OverlayMotion.DoneHalfWidth(0), 6);
        Assert.InRange(OverlayMotion.DoneHalfWidth(0.08), 148.4, 148.7);
        Assert.Equal(0, OverlayMotion.DoneHalfWidth(0.16), 6);

        Assert.Equal(0, OverlayMotion.DoneCheck(0.06), 6);
        Assert.Equal(0.5, OverlayMotion.DoneCheck(0.13), 6);
        Assert.Equal(1, OverlayMotion.DoneCheck(0.25), 6);

        Assert.Equal(1, OverlayMotion.DoneAlpha(0.1));
        Assert.Equal(0.5, OverlayMotion.DoneAlpha(0.24), 6);
        Assert.Equal(0, OverlayMotion.DoneAlpha(0.3), 6);
    }

    [Fact]
    public void CancelledSlidesOutOnTheAccelerateCurve()
    {
        Assert.Equal(0, OverlayMotion.CancelledProgress(0), 6);
        Assert.InRange(OverlayMotion.CancelledProgress(0.12), 0.32, 0.33);
        Assert.Equal(1, OverlayMotion.CancelledProgress(0.24), 6);
        Assert.Equal(0.5, OverlayMotion.CancelledFade(0.12), 6);
    }

    [Theory]
    [InlineData(0.0, 0.35)]
    [InlineData(0.045, 1)]
    [InlineData(0.135, 0.35)]
    [InlineData(0.225, 1)]
    [InlineData(0.315, 0.35)]
    [InlineData(0.4, 1)]
    public void TheErrorLineBlinksTwice(double since, double brightness) =>
        Assert.Equal(brightness, OverlayMotion.ErrorFlash(since));

    [Fact]
    public void TheErrorHoldsForTwoSecondsThenFades()
    {
        Assert.Equal(1, OverlayMotion.ErrorAlpha(1.9));
        Assert.Equal(0.5, OverlayMotion.ErrorAlpha(2.15), 6);
        Assert.Equal(0, OverlayMotion.ErrorAlpha(2.3), 6);
    }
}
