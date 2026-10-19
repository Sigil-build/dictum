using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

public class CubicBezierTests
{
    // Values from the CSS definition of cubic-bezier.
    [Theory]
    [InlineData(0.1, 0.4463)]
    [InlineData(0.25, 0.6906)]
    [InlineData(0.5, 0.8899)]
    [InlineData(0.9, 0.99651)]
    public void DecelerateIsTheTokensCurve(double progress, double eased) =>
        Assert.Equal(eased, CubicBezier.Decelerate.Ease(progress), 3);

    [Theory]
    [InlineData(0.1, 0.0184)]
    [InlineData(0.5, 0.3248)]
    [InlineData(0.9, 0.8438)]
    public void AccelerateIsTheTokensCurve(double progress, double eased) =>
        Assert.Equal(eased, CubicBezier.Accelerate.Ease(progress), 3);

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void ProgressOutsideZeroToOneIsClamped(double progress, double eased)
    {
        Assert.Equal(eased, CubicBezier.Decelerate.Ease(progress));
        Assert.Equal(eased, CubicBezier.Accelerate.Ease(progress));
    }
}
