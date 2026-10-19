using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Overlay;

public class OverlayControllerTests
{
    private const float Loud = 0.0501187f;

    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly FakeSurface _surface = new();
    private readonly FakeDesktop _desktop;
    private OverlayProbe _probe = new(new ScreenHint(null, "DP-2"), false);
    private bool _highContrast;

    public OverlayControllerTests() => _desktop = new FakeDesktop(() => _probe);

    private OverlayController Create(DictumConfig? config = null)
    {
        var controller = new OverlayController(() => _surface, _desktop, () => _highContrast, _clock, NullLog.Instance);
        controller.OnConfig(config ?? new DictumConfig());
        return controller;
    }

    private static IpcEvent State(string state, string? profile = "dictate") => new("state", State: state, Profile: profile);

    private static IpcEvent Finished(string outcome, string? message = null, string? language = null) =>
        new("finished", Outcome: outcome, Message: message, Language: language);

    private static DictumConfig WithOverlay(OverlaySettings overlay) => new() { General = new GeneralSettings { Overlay = overlay } };

    /// <summary>Moves the clock on and draws one frame, as the window's loop does.</summary>
    private OverlayScene Frame(double seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        return _surface.Next!();
    }

    [Fact]
    public void ARecordingShowsTheOverlayOnThePointersScreen()
    {
        var controller = Create();

        controller.OnEvent(State("Recording"));

        var shown = Assert.Single(_surface.Presented);
        Assert.True(shown.X >= 2560, $"on DP-2: {shown}");
        Assert.Equal(OverlayPhase.Listening, Frame(0.02).Phase);
    }

    [Fact]
    public void WithoutAHintTheOverlayGoesToThePrimaryScreen()
    {
        _probe = OverlayProbe.Unknown;
        var controller = Create();

        controller.OnEvent(State("Recording"));

        Assert.True(Assert.Single(_surface.Presented).X < 2560);
    }

    [Fact]
    public void AProbeThatDoesNotAnswerStillShowsTheOverlay()
    {
        _desktop.Slow = true;
        var controller = Create();

        controller.OnEvent(State("Recording"));

        Assert.True(Assert.Single(_surface.Presented).X < 2560);
    }

    [Fact]
    public void TheOverlayFollowsTheDictationAndHides()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        controller.OnEvent(State("Transcribing"));
        Assert.Equal(OverlayPhase.Transcribing, Frame(0.1).Phase);

        controller.OnEvent(State("Processing"));
        Assert.Equal(OverlayPhase.CleaningUp, Frame(0.1).Phase);

        controller.OnEvent(State("Output"));
        controller.OnEvent(State("Idle"));
        controller.OnEvent(Finished(DictationOutcome.Pasted));
        Assert.Equal(OverlayPhase.Done, Frame(0.1).Phase);
        Assert.Equal(OverlayPhase.Hidden, Frame(0.3).Phase);
    }

    [Fact]
    public void FramesFeedTheWavesWhileListening()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        var scene = OverlayScene.Hidden;
        for (var i = 0; i < 20; i++)
        {
            controller.OnFrame(new IpcEvent("frame", Rms: Loud, Bands: new float[24]));
            scene = Frame(0.02);
        }

        Assert.True(scene.Level > 0.99, $"level {scene.Level}");
    }

    // Review focus 2: Settings' level meter (monitor-start) and test-stt send frames while the daemon is Idle.
    [Fact]
    public void FramesOutsideADictationDoNotShowTheOverlay()
    {
        var controller = Create();

        controller.OnFrame(new IpcEvent("frame", Rms: Loud, Bands: new float[24]));
        controller.OnEvent(State("Idle"));
        Assert.Empty(_surface.Presented);

        controller.OnEvent(State("Recording"));
        Assert.Equal(0, Frame(0.02).Level);
    }

    // Review focus 3.
    [Fact]
    public void ALostLinkHidesTheOverlay()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        controller.OnLink(new DaemonLink(DaemonLinkState.Restarting));

        Assert.Equal(1, _surface.Dismissed);
        Assert.Equal(OverlayPhase.Hidden, controller.Phase);
    }

    [Fact]
    public void StoppingHidesTheOverlay()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        controller.OnEvent(new IpcEvent("stopping", Reason: "shutdown"));

        Assert.Equal(1, _surface.Dismissed);
        Assert.Equal(OverlayPhase.Hidden, controller.Phase);
    }

    // Review focus 5.
    [Fact]
    public void TurningTheOverlayOffHidesItAndKeepsItHidden()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        controller.OnConfig(WithOverlay(new OverlaySettings { Enabled = false }));
        controller.OnEvent(State("Recording"));

        Assert.Equal(1, _surface.Dismissed);
        Assert.Single(_surface.Presented);
        Assert.Equal(OverlayPhase.Hidden, controller.Phase);
    }

    [Theory]
    [InlineData(DictationOutcome.Empty, null, "Nothing recognised")]
    [InlineData(DictationOutcome.Error, "No microphone", "No microphone")]
    [InlineData(DictationOutcome.Error, null, "Dictation failed")]
    [InlineData(DictationOutcome.Error, "  ", "Dictation failed")]
    public void ErrorsShowTheirMessage(string outcome, string? message, string shown)
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));
        controller.OnEvent(State("Idle"));

        controller.OnEvent(Finished(outcome, message));

        var scene = Frame(0.5);
        Assert.Equal(OverlayPhase.Error, scene.Phase);
        Assert.Equal(shown, scene.Message);
    }

    [Fact]
    public void CancelledShowsCancelled()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));
        controller.OnEvent(State("Idle"));

        controller.OnEvent(Finished(DictationOutcome.Cancelled));

        Assert.Equal(OverlayPhase.Cancelled, Frame(0.05).Phase);
    }

    [Fact]
    public void AListProfileShowsItsLanguagesThenThePickedOne()
    {
        var controller = Create(new DictumConfig { Profiles = [new ProfileConfig { Id = "both", Stt = "local", Language = "uk, en" }] });

        controller.OnEvent(State("Recording", "both"));
        Assert.Equal("UK/EN", Frame(0.02).Chip);

        controller.OnEvent(State("Idle", "both"));
        controller.OnEvent(Finished(DictationOutcome.Pasted, language: "uk"));
        Assert.Equal("UK", Frame(0.02).Chip);
    }

    [Fact]
    public void ThePaletteComesFromTheConfigAndHighContrastForcesMist()
    {
        var controller = Create(WithOverlay(new OverlaySettings { Palette = OverlayPalette.Ember }));
        controller.OnEvent(State("Recording"));
        Assert.Equal(OverlayPalette.Ember, Frame(0.02).Palette);

        _highContrast = true;

        Assert.Equal(OverlayPalette.Mist, Frame(0.02).Palette);
    }

    [Fact]
    public void ReducedMotionComesFromTheDesktop()
    {
        _probe = _probe with { ReducedMotion = true };
        var controller = Create();

        controller.OnEvent(State("Recording"));

        Assert.True(Frame(0.02).ReducedMotion);
    }

    [Fact]
    public void SettingsChangesMoveAVisibleOverlay()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        controller.OnConfig(WithOverlay(new OverlaySettings { Position = OverlayPosition.Top, Size = OverlaySize.Large, Opacity = 0.5 }));

        Assert.Equal(2, _surface.Presented.Count);
        Assert.Equal(5, _surface.Presented[1].Y);
        Assert.Equal(1.25, _surface.Zoom);
        Assert.Equal(0.5, _surface.Opacity);
    }

    [Fact]
    public void ScreenChangesPlaceAVisibleOverlayAgain()
    {
        var controller = Create();
        controller.OnEvent(State("Recording"));

        _surface.ChangeScreens();
        Assert.Equal(2, _surface.Presented.Count);

        controller.OnLink(new DaemonLink(DaemonLinkState.Failed));
        _surface.ChangeScreens();
        Assert.Equal(2, _surface.Presented.Count);
    }

    [Fact]
    public void AnAppThatConnectsMidDictationShowsIt()
    {
        var controller = Create();

        controller.OnLink(new DaemonLink(DaemonLinkState.Connected, new DaemonStatus("Recording", "dictate", [], "portal", "portal", 42)));

        Assert.Single(_surface.Presented);
        Assert.Equal(OverlayPhase.Listening, controller.Phase);
    }
}
