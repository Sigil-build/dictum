using Dictum.Core.Audio;

namespace Dictum.Core.Tests.Audio;

public sealed class FrameRecordingTests
{
    private static readonly float[] FirstBands = [0.5f, 0.25f];
    private static readonly float[] SecondBands = [0, 1];

    [Fact]
    public void ADumpIsWrittenInTheFramesFormatAndReadsBack()
    {
        AudioFrame[] captured =
        [
            new(0.1f, FirstBands, TimeSpan.FromMilliseconds(20.4)),
            new(0.2f, SecondBands, TimeSpan.FromMilliseconds(40)),
        ];

        var json = FrameRecording.Of([.. captured.Select(RecordedFrame.From)]).ToJson();
        var read = FrameRecording.Parse(json);

        Assert.Equal("""{"framesPerSecond":50,"bands":2,"frames":[{"t":0.02,"rms":0.1,"bands":[0.5,0.25]},{"t":0.04,"rms":0.2,"bands":[0,1]}]}""", json);
        Assert.Equal((AudioFrame.PerSecond, 2, 2), (read.FramesPerSecond, read.Bands, read.Frames.Count));
        Assert.Equal([0.02, 0.04], read.Frames.Select(f => f.T));
        Assert.Equal([0.1f, 0.2f], read.Frames.Select(f => f.Rms));
        Assert.Equal(FirstBands, read.Frames[0].Bands);
    }
}
