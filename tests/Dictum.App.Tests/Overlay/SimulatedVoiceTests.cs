using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

public sealed class SimulatedVoiceTests
{
    [Fact]
    public void ItSpeaksForTwoPointThreeSecondsOfEveryCycle()
    {
        var bands = new float[OverlayTokens.BandCount];
        var loudest = 0f;
        for (var t = 0.0; t < SimulatedVoice.Speaking; t += 0.02)
        {
            loudest = Math.Max(loudest, SimulatedVoice.Sample(t, bands));
        }

        for (var t = 2.4; t < SimulatedVoice.Cycle; t += 0.02)
        {
            Assert.Equal(0.0005f, SimulatedVoice.Sample(t + SimulatedVoice.Cycle, bands));
            Assert.All(bands, b => Assert.Equal(0f, b));
        }

        // Peaks near −29 dBFS: the overlay's level reaches about 0.9.
        Assert.InRange(loudest, 0.025f, 0.0355f);
    }

    [Fact]
    public void TheBandsStayBetweenZeroAndOne()
    {
        var bands = new float[OverlayTokens.BandCount];
        for (var t = 0.0; t < 10; t += 0.05)
        {
            SimulatedVoice.Sample(t, bands);
            Assert.All(bands, b => Assert.InRange(b, 0f, 1f));
        }
    }
}
