using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

public class WaveShapesTests
{
    private static readonly double[] Xs = Enumerable.Range(0, 121).Select(i => i * 4.0).ToArray();

    [Fact]
    public void TheEnvelopeIsFlatAtTheEdgesAndFullInTheMiddle()
    {
        Assert.Equal(0.0132, WaveShapes.Envelope(0), 4);
        Assert.Equal(0.0878, WaveShapes.Envelope(60), 4);
        Assert.Equal(1, WaveShapes.Envelope(240), 9);
        Assert.Equal(WaveShapes.Envelope(0), WaveShapes.Envelope(480), 9);
    }

    [Fact]
    public void EachWaveAveragesItsOwnBands()
    {
        var bands = new double[24];
        bands[5] = 1;
        bands[6] = 1;

        Assert.Equal(1.0 / 6, WaveShapes.BandMean(bands, OverlayTokens.Waves[0]), 9);
        Assert.Equal(2.0 / 6, WaveShapes.BandMean(bands, OverlayTokens.Waves[1]), 9);
        Assert.Equal(0, WaveShapes.BandMean(bands, OverlayTokens.Waves[4]), 9);
    }

    [Fact]
    public void ListeningAmplitudeFollowsLevelBandsAndBreath()
    {
        Assert.Equal(21.4, WaveShapes.ListeningAmplitude(0, 0.5, 0.3, 0), 6);
        Assert.Equal(13.7748, WaveShapes.ListeningAmplitude(2, 0.5, 0.3, 1), 4);
    }

    [Fact]
    public void SilenceBreathesOnAFourSecondSine()
    {
        Assert.Equal(1.92, WaveShapes.QuietAmplitude(1, 1), 9);
        Assert.Equal(0, WaveShapes.QuietAmplitude(1, 3), 9);
        Assert.Equal(0.95, WaveShapes.QuietAlpha(0));
        Assert.Equal(0.35, WaveShapes.QuietAlpha(3));
    }

    [Fact]
    public void AWaveRidesOnTheCentreLine()
    {
        // Wave 1 at x 120 is a quarter cycle in, where the envelope is 0.339.
        Assert.Equal(50.783, WaveShapes.WaveY(0, 120, 20, 0), 3);
        Assert.Equal(44, WaveShapes.WaveY(0, 120, 0, 0), 9);
    }

    [Theory]
    [InlineData(0, -0.15)]
    [InlineData(0.65, 0.5)]
    [InlineData(1.3, -0.15)]
    [InlineData(1.95, 0.5)]
    public void TheTranscribingBumpCrossesOncePerPeriod(double time, double position) =>
        Assert.Equal(position, WaveShapes.BumpPosition(time), 6);

    [Fact]
    public void TheBumpLiftsTheLineWhereItIs()
    {
        var lowest = Xs.MinBy(x => WaveShapes.TranscribingY(x, 0.65));

        Assert.InRange(lowest, 228, 252);
        Assert.True(WaveShapes.TranscribingY(240, 0.65) < 44 - 7);
    }

    [Fact]
    public void CleaningUpStaysWithinSevenUnitsAndStartsFlat()
    {
        Assert.All(Xs, x => Assert.Equal(44, WaveShapes.CleaningY(x, 0.18, 0), 9));
        Assert.All(Xs, x => Assert.InRange(WaveShapes.CleaningY(x, 0.18, 1), 37, 51));
        Assert.True(Xs.Max(x => Math.Abs(WaveShapes.CleaningY(x, 1 / (4 * 1.4), 1) - 44)) > 6);
    }

    [Fact]
    public void ATraceEndsExactlyAtItsLastX()
    {
        var points = new List<(double X, bool First)>();

        WaveShapes.Trace(0, 10, x => x, (x, _, first) => points.Add((x, first)));
        Assert.Equal(new[] { (0.0, true), (4.0, false), (8.0, false), (10.0, false) }, points);

        points.Clear();
        WaveShapes.Trace(0, 8, x => x, (x, _, first) => points.Add((x, first)));
        Assert.Equal([0.0, 4.0, 8.0], points.Select(p => p.X));
    }

    [Fact]
    public void TheCheckIsDrawnAlongItsLength()
    {
        Assert.Equal(2, WaveShapes.CheckPath(0.2).Count);

        var full = WaveShapes.CheckPath(1);
        Assert.Equal(3, full.Count);
        Assert.Equal(8, full[^1].X, 6);
        Assert.Equal(-6, full[^1].Y, 6);
    }
}
