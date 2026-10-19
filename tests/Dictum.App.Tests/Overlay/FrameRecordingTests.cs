using Dictum.Core.Audio;

namespace Dictum.App.Tests.Overlay;

public class FrameRecordingTests
{
    [Fact]
    public void TheDesignRecordingsLoad()
    {
        var uk = Recordings.Load("frames-uk.json");

        Assert.Equal(50, uk.FramesPerSecond);
        Assert.Equal(701, uk.Frames.Count);
        Assert.Equal(24, uk.Frames[0].Bands.Length);
        Assert.Equal(TimeSpan.FromSeconds(14.02), uk.Duration);
    }

    [Fact]
    public void FramesPlayByIndexAsThePrototypeDoes()
    {
        var recording = FrameRecording.Parse("""{"framesPerSecond":50,"bands":1,"frames":[{"t":0.02,"rms":0.1,"bands":[0]},{"t":0.04,"rms":0.2,"bands":[0]}]}""");

        Assert.Equal(0.1f, recording.At(TimeSpan.FromMilliseconds(19))!.Rms);
        Assert.Equal(0.2f, recording.At(TimeSpan.FromMilliseconds(20))!.Rms);
        Assert.Null(recording.At(TimeSpan.FromMilliseconds(40)));
        Assert.Null(recording.At(TimeSpan.FromMilliseconds(-1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("{}")]
    [InlineData("""{"framesPerSecond":50,"frames":[]}""")]
    [InlineData("""{"framesPerSecond":0,"frames":[{"t":0,"rms":0,"bands":[]}]}""")]
    public void AnythingElseIsNotAFramesFile(string json) => Assert.Throws<FormatException>(() => FrameRecording.Parse(json));
}
