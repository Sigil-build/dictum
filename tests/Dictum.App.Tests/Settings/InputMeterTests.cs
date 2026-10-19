using Dictum.App.Settings;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Settings;

public sealed class InputMeterTests
{
    private static float Rms(double db) => (float)Math.Pow(10, db / 20);

    [Theory]
    [InlineData(-70, 0)]
    [InlineData(-60, 0)]
    [InlineData(-31, 0.58)]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    public void TheLevelSpansMinusSixtyToMinusTenDbfs(double db, double level) =>
        Assert.Equal(level, InputMeter.Target(Rms(db)), 3);

    [Fact]
    public void SteadySpeechLightsTheDesignsSegments()
    {
        var meter = new InputMeter();
        for (var i = 1; i <= 40; i++)
        {
            meter.Update(Rms(-31), TimeSpan.FromMilliseconds(50 * i));
        }

        Assert.Equal(17, InputMeter.LitFor(meter.Level));
        Assert.Equal(-31, meter.Db);
    }

    [Fact]
    public void ItRisesFastAndFallsSlowly()
    {
        var meter = new InputMeter();
        meter.Update(Rms(-10), TimeSpan.FromMilliseconds(50));
        var rise = meter.Level;
        for (var i = 2; i <= 20; i++)
        {
            meter.Update(Rms(-10), TimeSpan.FromMilliseconds(50 * i));
        }

        meter.Update(0, TimeSpan.FromMilliseconds(1050));

        // One 50 ms step: 1 − e^(−50/30) up, e^(−50/160) of the way back down.
        Assert.InRange(rise, 0.80, 0.82);
        Assert.InRange(meter.Level, 0.72, 0.74);
    }

    [Fact]
    public void ResetDropsToSilence()
    {
        var meter = new InputMeter();
        meter.Update(Rms(-20), TimeSpan.FromMilliseconds(50));

        meter.Reset();

        Assert.Equal(0, meter.Level);
        Assert.Equal(-60, meter.Db);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.58, 17)]
    [InlineData(1.0, 30)]
    [InlineData(1.5, 30)]
    public void LevelsLightWholeSegments(double level, int lit) => Assert.Equal(lit, InputMeter.LitFor(level));

    [Theory]
    [InlineData(-31, "−31 dBFS")]
    [InlineData(0, "0 dBFS")]
    public void TheReadoutUsesTheTypographicMinus(int db, string text) => Assert.Equal(text, InputMeter.Readout(db));

    [Fact]
    public void TheHubGivesTheNewestFrameUntilItIsStale()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var hub = new FrameHub(clock);
        Assert.Equal(0f, hub.Rms(InputMeter.Stale));

        hub.Publish(new IpcEvent(IpcEventKind.Frame, Rms: 0.1f));
        hub.Publish(new IpcEvent(IpcEventKind.Frame, Rms: 0.2f));
        Assert.Equal(0.2f, hub.Rms(InputMeter.Stale));

        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0f, hub.Rms(InputMeter.Stale));
    }
}
