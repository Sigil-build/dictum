using Dictum.Audio.PortAudio;

namespace Dictum.Core.Tests.Audio;

public class SoundPlayerTests
{
    [Theory]
    [InlineData(SoundCue.Start)]
    [InlineData(SoundCue.Stop)]
    [InlineData(SoundCue.Error)]
    public void CuesAreShortQuietAndStartAndEndSilently(SoundCue cue)
    {
        var samples = SoundPlayer.Synthesize(cue);

        Assert.InRange(samples.Length / 48_000.0, 0.1, 0.4);
        Assert.All(samples, s => Assert.InRange(s, -0.25f, 0.25f));
        Assert.Equal(0f, samples[0], 0.001f);
        Assert.Equal(0f, samples[^1], 0.02f);
    }
}
