using Dictum.App.Daemon;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Setup;

public sealed class SetupReadyStepTests
{
    private readonly FakeChannel _daemon = new() { Link = new DaemonLink(DaemonLinkState.NotConfigured) };
    private readonly DictumConfig _config = DefaultConfig.Create();
    private int _restarts;

    private SetupReadyStep Step(TrayHint? tray = null)
    {
        var step = new SetupReadyStep(tray ?? new TrayHint(TrayDesktop.Other, () => false, null), _daemon, () => _restarts++);
        step.Load(_config, null);
        step.OnShown();
        return step;
    }

    private DaemonLink Connected(bool registered = true, bool? warmedUp = true, bool paused = false) =>
        new(DaemonLinkState.Connected, new DaemonStatus(
            "Idle", null, [new ProfileStatus(_config.Profiles[0].Id, _config.Profiles[0].Hotkey, "PushToTalk", registered)],
            "hook", "paste", 4242, Paused: paused, WarmedUp: warmedUp));

    private static (string, bool, bool, bool) Line(SetupReadyStep step) => (step.DaemonText, step.IsReady, step.HasProblem, step.CanRestart);

    // Final review M2: opening the OS page is a Process.Start that can fail; the command never throws.
    [Fact]
    public void OpeningTheTraySettingsNeverThrows()
    {
        var step = Step(new TrayHint(TrayDesktop.Windows, () => true, () => throw new InvalidOperationException("No shell.")));

        step.OpenTraySettingsCommand.Execute(null);

        Assert.Equal("Ready", step.Name);
    }

    [Fact]
    public void UntilTheDaemonAnswersStepEightSaysItIsStarting() =>
        Assert.Equal(("Starting Dictum…", false, false, false), Line(Step()));

    // Review focus 1: a daemon that has just started answers before it registered its hotkeys.
    [Fact]
    public void AnEmptyProfileListIsNotKnownYet()
    {
        var step = Step();

        step.OnLink(FakeChannel.Connected());

        Assert.Equal(("Starting Dictum…", false, false, false), Line(step));
    }

    // Review focus 1.
    [Fact]
    public void RunningOnlyAfterTheWarmUp()
    {
        var step = Step();

        step.OnLink(Connected(warmedUp: false));
        Assert.Equal(("Loading the speech model. You can dictate once it is in memory.", false, false, false), Line(step));

        step.OnLink(Connected(warmedUp: true));
        Assert.Equal(("Dictum is running. Try it in the box below.", true, false, false), Line(step));
    }

    // Review focus 1: an older daemon does not say; it is taken as running.
    [Fact]
    public void ADaemonThatDoesNotSayIsTakenAsRunning()
    {
        var step = Step();

        step.OnLink(Connected(warmedUp: null));

        Assert.True(step.IsReady);
    }

    // Review focus 3.
    [Fact]
    public void ARefusedHotkeySaysSo()
    {
        var step = Step();

        step.OnLink(Connected(registered: false));

        Assert.Equal(("The daemon could not register this hotkey; another app may already use it.", false, true, false), Line(step));
    }

    // Review focus 3.
    [Fact]
    public void PausedHotkeysSaySo()
    {
        var step = Step();

        step.OnLink(Connected(paused: true));

        Assert.Equal(("Hotkeys are paused. Resume them from the tray menu.", false, true, false), Line(step));
    }

    // Review focus 3.
    [Fact]
    public void AFailedStartOffersRestart()
    {
        var step = Step();

        step.OnLink(new DaemonLink(DaemonLinkState.Failed, Problem: "The daemon exited while starting (code 1)."));
        Assert.Equal(("Dictum could not start: The daemon exited while starting (code 1).", false, true, true), Line(step));

        step.RestartCommand.Execute(null);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public void StepEightReadsTheLinkWhenShown()
    {
        _daemon.Link = Connected();

        Assert.True(Step().IsReady);
    }

    [Fact]
    public void TheTryLineFollowsADictation()
    {
        var step = Step();
        Assert.Null(step.TryResult);

        step.OnEvent(IpcEvent.ForState(PipelineState.Recording, "dictate", "PushToTalk"));
        Assert.Equal("Listening", step.TryResult);
        step.OnEvent(IpcEvent.ForState(PipelineState.Transcribing, "dictate", "PushToTalk"));
        Assert.Equal("Transcribing…", step.TryResult);
        step.OnEvent(IpcEvent.ForState(PipelineState.Idle, "dictate", "PushToTalk"));
        Assert.Equal("Transcribing…", step.TryResult);
        step.OnEvent(new IpcEvent("finished", Outcome: DictationOutcome.Pasted));
        Assert.Equal("Done. The text went where the cursor was.", step.TryResult);
        step.OnEvent(new IpcEvent("finished", Outcome: DictationOutcome.Empty));
        Assert.Equal("Nothing was recognised. Speak a little closer to the microphone and try again.", step.TryResult);
        step.OnEvent(new IpcEvent("finished", Outcome: DictationOutcome.Error, Message: "The microphone is busy."));
        Assert.Equal("The dictation failed: The microphone is busy.", step.TryResult);
    }
}
