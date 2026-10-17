using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

public class LevelMeterTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.001f, 0)]
    [InlineData(0.01f, 0.588)]
    [InlineData(0.0199526f, 0.765)]
    [InlineData(0.0501187f, 1)]
    [InlineData(1f, 1)]
    public void TheLevelMapsMinusSixtyToMinusTwentySixDecibelsOntoZeroToOne(float rms, double level) =>
        Assert.Equal(level, LevelMeter.Target(rms), 3);

    [Fact]
    public void TheLevelRisesWithTheAttackAndFallsWithTheRelease()
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);

        meter.Update(1f, [], TimeSpan.FromMilliseconds(30));
        Assert.Equal(1 - Math.Exp(-1), meter.Level, 3);

        // 160 ms later, but one step never covers more than 50 ms.
        meter.Update(0f, [], TimeSpan.FromMilliseconds(190));
        Assert.Equal((1 - Math.Exp(-1)) * Math.Exp(-0.05 / 0.16), meter.Level, 3);
    }

    [Fact]
    public void BandsAreSmoothedLikeTheLevelAndMissingBandsCountAsSilence()
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);

        meter.Update(0.01f, [1f, 0.5f], TimeSpan.FromMilliseconds(30));

        Assert.Equal(24, meter.Bands.Count);
        Assert.Equal(0.632, meter.Bands[0], 3);
        Assert.Equal(0.316, meter.Bands[1], 3);
        Assert.Equal(0, meter.Bands[23]);
    }

    [Fact]
    public void SilenceNeedsMoreThanHalfASecondUnderTheThreshold()
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);

        meter.Update(0f, [], TimeSpan.FromMilliseconds(10));
        meter.Update(0f, [], TimeSpan.FromMilliseconds(510));
        Assert.False(meter.Quiet);

        meter.Update(0f, [], TimeSpan.FromMilliseconds(520));
        Assert.True(meter.Quiet);

        meter.Update(0.01f, [], TimeSpan.FromMilliseconds(540));
        Assert.False(meter.Quiet);
    }

    [Fact]
    public void ResetStartsFromSilence()
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);
        meter.Update(0.01f, [1f], TimeSpan.FromMilliseconds(30));

        meter.Reset(TimeSpan.FromSeconds(1));

        Assert.Equal(0, meter.Level);
        Assert.Equal(0, meter.Bands[0]);
        Assert.False(meter.Quiet);
    }

    [Fact]
    public void AClockThatGoesBackDoesNotMoveTheLevel()
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.FromSeconds(1));

        meter.Update(1f, [], TimeSpan.FromSeconds(0.5));

        Assert.Equal(0, meter.Level);
    }

    // Measured by replaying the recordings through the prototype's maths (plan rulings).
    [Theory]
    [InlineData("frames-uk.json", 0.574, 0.769)]
    [InlineData("frames-en.json", 0.550, 0.748)]
    public void VoicedFramesSitAtTheMeasuredLevels(string file, double median, double p95)
    {
        var levels = Recordings.Load(file).Frames
            .Where(f => f.Bands.Any(b => b > 0))
            .Select(f => LevelMeter.Target(f.Rms))
            .Order()
            .ToArray();

        Assert.InRange(levels[levels.Length / 2], median - 0.005, median + 0.005);
        Assert.InRange(levels[(int)Math.Round(0.95 * (levels.Length - 1))], p95 - 0.005, p95 + 0.005);
    }

    [Theory]
    [InlineData("frames-uk.json", new[] { 0.53, 1.13, 7.32, 9.58 })]
    [InlineData("frames-en.json", new[] { 0.53, 1.35, 15.93, 16.20 })]
    public void SilenceFiresOnTheLeadingSilenceAndTheLongPausesOnly(string file, double[] edges)
    {
        var (spans, loudest) = Replay(Recordings.Load(file));

        Assert.Equal(edges.Length / 2, spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            Assert.InRange(spans[i].Start, edges[2 * i] - 0.02, edges[2 * i] + 0.02);
            Assert.InRange(spans[i].End, edges[2 * i + 1] - 0.02, edges[2 * i + 1] + 0.02);
        }

        // The loudest syllables reach high without pinning the waves at the top.
        Assert.InRange(loudest, 0.75, 0.95);
    }

    [Fact]
    public void TheSilenceRecordingStaysAtZero()
    {
        var recording = Recordings.Load("frames-silence.json");

        var (spans, loudest) = Replay(recording);

        Assert.Equal(0, loudest, 3);
        var span = Assert.Single(spans);
        Assert.InRange(span.Start, 0.51, 0.55);
        Assert.Equal(recording.Duration.TotalSeconds, span.End, 2);
    }

    /// <summary>Plays a recording into the meter at 60 rendered frames a second, as the overlay does.</summary>
    private static (List<(double Start, double End)> Spans, double Loudest) Replay(FrameRecording recording)
    {
        var meter = new LevelMeter();
        meter.Reset(TimeSpan.Zero);
        var spans = new List<(double Start, double End)>();
        double? start = null;
        var loudest = 0.0;
        var frames = (int)(recording.Duration.TotalSeconds * 60);
        for (var k = 1; k <= frames; k++)
        {
            var now = TimeSpan.FromSeconds(k / 60.0);
            var frame = recording.At(now);
            meter.Update(frame?.Rms ?? 0, frame?.Bands ?? [], now);
            loudest = Math.Max(loudest, meter.Level);
            if (meter.Quiet && start is null)
            {
                start = now.TotalSeconds;
            }
            else if (!meter.Quiet && start is { } began)
            {
                spans.Add((began, now.TotalSeconds));
                start = null;
            }
        }

        if (start is { } open)
        {
            spans.Add((open, recording.Duration.TotalSeconds));
        }

        return (spans, loudest);
    }
}
