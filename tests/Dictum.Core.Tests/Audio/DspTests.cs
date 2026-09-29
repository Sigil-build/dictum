using Dictum.Audio;
using Dictum.Core.Audio;

namespace Dictum.Core.Tests.Audio;

public class DspTests
{
    [Theory]
    [InlineData(48_000, 16_000)]
    [InlineData(44_100, 16_000)]
    [InlineData(8_000, 16_000)]
    public void ResamplingKeepsFrequencyAndAmplitude(int from, int to)
    {
        var input = Signals.Sine(1_000, from, 1.0);

        var output = Resampler.Resample(input, from, to);

        Assert.InRange(output.Length, to - 1, to + 1);
        var steady = output.AsSpan(to / 10, to * 8 / 10);
        Assert.Equal(1_000, Signals.Frequency(steady, to), tolerance: 5.0);
        Assert.Equal(Levels.Rms(input), Levels.Rms(steady), 0.01f);
    }

    [Fact]
    public void DownsamplingSuppressesContentAboveTheNewNyquist()
    {
        var input = Signals.Sine(11_000, 48_000, 0.5);

        var output = Resampler.Resample(input, 48_000, 16_000);

        Assert.True(Levels.ToDb(Levels.Rms(output.AsSpan(800, 6_400))) < -40);
    }

    [Fact]
    public void TrimmerRemovesSilenceButKeepsPadding()
    {
        var samples = Signals.Silence(16_000, 1.0)
            .Concat(Signals.Sine(300, 16_000, 1.0))
            .Concat(Signals.Silence(16_000, 2.0))
            .ToArray();

        var trimmed = SilenceTrimmer.Trim(new AudioClip(samples), paddingMs: 250);

        Assert.Equal(1.5, trimmed.Duration.TotalSeconds, 0.05);
    }

    [Fact]
    public void TrimmerReturnsEmptyClipForPureSilence() =>
        Assert.Equal(TimeSpan.Zero, SilenceTrimmer.Trim(new AudioClip(Signals.Silence(16_000, 1.0))).Duration);

    [Fact]
    public void SilenceDetectorFiresOnlyAfterSpeechThenQuiet()
    {
        var detector = new SilenceDetector(TimeSpan.FromSeconds(1));
        var frame = TimeSpan.FromMilliseconds(20);
        var fired = new List<bool>();

        Push(detector, 0.001f, 100, frame, fired);   // 2 s of room noise
        Assert.DoesNotContain(true, fired);

        Push(detector, 0.2f, 50, frame, fired);      // 1 s of speech
        Push(detector, 0.001f, 49, frame, fired);    // 0.98 s of quiet
        Assert.DoesNotContain(true, fired);

        Assert.True(detector.Push(0.001f, frame));   // reaches 1 s of quiet
    }

    [Fact]
    public void SpectrumPeaksInTheBandOfTheTone()
    {
        var analyzer = new SpectrumAnalyzer(48_000, 960, bandCount: 24);
        var lowTone = analyzer.Analyze(Signals.Sine(200, 48_000, 0.02));
        var highTone = analyzer.Analyze(Signals.Sine(4_000, 48_000, 0.02));

        var lowPeak = Array.IndexOf(lowTone, lowTone.Max());
        var highPeak = Array.IndexOf(highTone, highTone.Max());

        Assert.Equal(24, lowTone.Length);
        Assert.True(lowPeak < highPeak, $"low tone peaked at band {lowPeak}, high tone at {highPeak}");
        Assert.InRange(highTone.Max(), 0.5f, 1f);
    }

    private static void Push(SilenceDetector detector, float rms, int frames, TimeSpan frame, List<bool> fired)
    {
        for (var i = 0; i < frames; i++)
        {
            fired.Add(detector.Push(rms, frame));
        }
    }
}
