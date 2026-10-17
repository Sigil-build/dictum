using Dictum.App.Overlay;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Overlay;

public class OverlayDemoTests
{
    [Fact]
    public void ParseReadsTheFileAndTheState()
    {
        Assert.Equal(new OverlayDemoOptions("a.json", "listening"), OverlayDemo.Parse(["--overlay-demo", "a.json"], TextWriter.Null));
        Assert.Equal(new OverlayDemoOptions("a.json", "done"), OverlayDemo.Parse(["--overlay-demo", "a.json", "--state", "done"], TextWriter.Null));
    }

    [Theory]
    [InlineData(new[] { "--overlay-demo" }, "Usage")]
    [InlineData(new[] { "--overlay-demo", "--state", "done" }, "Usage")]
    [InlineData(new[] { "--overlay-demo", "a.json", "--state", "nope" }, "listening, silence")]
    [InlineData(new[] { "--overlay-demo", "a.json", "--state" }, "listening, silence")]
    public void ParseExplainsWhatIsWrong(string[] args, string hint)
    {
        var error = new StringWriter();

        Assert.Null(OverlayDemo.Parse(args, error));
        Assert.Contains(hint, error.ToString());
    }

    [Fact]
    public void TheDemoKeepsTheUsersLookShowsTheOverlayAndListsTwoLanguages()
    {
        var user = new DictumConfig { General = new GeneralSettings { Overlay = new OverlaySettings { Palette = OverlayPalette.Ember, Enabled = false } } };

        var config = OverlayDemo.Config(user);

        Assert.Equal(OverlayPalette.Ember, config.General.Overlay.Palette);
        Assert.True(config.General.Overlay.Enabled);
        Assert.Equal("uk, en", Assert.Single(config.Profiles).Language);
    }

    [Theory]
    [InlineData("listening", new[] { OverlayPhase.Listening, OverlayPhase.Transcribing, OverlayPhase.CleaningUp, OverlayPhase.Done })]
    [InlineData("cleaning", new[] { OverlayPhase.CleaningUp, OverlayPhase.Done })]
    [InlineData("cancelled", new[] { OverlayPhase.Cancelled })]
    [InlineData("error", new[] { OverlayPhase.Error })]
    public void TheDemoPlaysItsStatesAndFinishes(string state, OverlayPhase[] expected)
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var surface = new FakeSurface();
        var controller = new OverlayController(() => surface, new FakeDesktop(() => OverlayProbe.Unknown), () => false, clock, NullLog.Instance);
        controller.OnConfig(OverlayDemo.Config(null));
        var recording = new FrameRecording(50, 24, [.. Enumerable.Repeat(new RecordedFrame(0, 0.05f, new float[24]), 5)]);
        var demo = new OverlayDemo(controller, recording, state, clock);
        demo.Begin();

        var seen = new List<OverlayPhase>();
        var finished = false;
        for (var tick = 0; tick < 500 && !finished; tick++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            finished = demo.Step(clock.GetUtcNow());
            if (surface.Next?.Invoke() is { Phase: not OverlayPhase.Hidden } scene && (seen.Count == 0 || seen[^1] != scene.Phase))
            {
                seen.Add(scene.Phase);
            }
        }

        Assert.True(finished, $"seen: {string.Join(", ", seen)}");
        Assert.Equal(expected, seen);
    }
}
