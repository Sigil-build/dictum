using Dictum.App.Overlay;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Overlay;

public class OverlayStateMachineTests
{
    private const float Loud = 0.0501187f;
    private static readonly float[] AllBands = Enumerable.Repeat(1f, 24).ToArray();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static OverlayScene Step(OverlayStateMachine machine, double seconds, float rms = 0, float[]? bands = null) =>
        machine.Advance(S(seconds), rms, bands ?? []);

    [Fact]
    public void ARecordingShowsTheWavesAndFadesThemIn()
    {
        var machine = new OverlayStateMachine();
        Assert.Equal(OverlayPhase.Hidden, Step(machine, 0).Phase);

        machine.Recording(S(1), []);

        var start = Step(machine, 1.06);
        Assert.Equal(OverlayPhase.Listening, start.Phase);
        Assert.Equal(0.8899, start.Appear, 3);
        Assert.Equal(1, Step(machine, 1.2).Appear);
    }

    [Fact]
    public void ADictationRunsThroughItsStatesAndHides()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);

        machine.Transcribing(S(2));
        Assert.Equal(OverlayPhase.Transcribing, Step(machine, 2.1).Phase);

        machine.Processing(S(3));
        Assert.Equal(OverlayPhase.CleaningUp, Step(machine, 3.1).Phase);

        machine.Idle(S(4));
        machine.Done(S(4), "en");
        Assert.Equal(OverlayPhase.Done, Step(machine, 4.299).Phase);
        Assert.Equal(OverlayPhase.Hidden, Step(machine, 4.3).Phase);
    }

    [Fact]
    public void ADictationWithoutCleanUpGoesStraightToDone()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        machine.Transcribing(S(1));
        machine.Idle(S(2));
        machine.Done(S(2), null);

        Assert.Equal(OverlayPhase.Done, Step(machine, 2.1).Phase);
    }

    [Fact]
    public void CancelledHidesAfter240Milliseconds()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        machine.Idle(S(1));
        machine.Cancelled(S(1));

        Assert.Equal(OverlayPhase.Cancelled, Step(machine, 1.239).Phase);
        Assert.Equal(OverlayPhase.Hidden, Step(machine, 1.24).Phase);
    }

    [Fact]
    public void AnErrorHoldsItsMessageThenFadesOut()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        machine.Idle(S(1));
        machine.Error(S(1), "No microphone");

        var scene = Step(machine, 2);
        Assert.Equal(OverlayPhase.Error, scene.Phase);
        Assert.Equal("No microphone", scene.Message);
        Assert.Equal(OverlayPhase.Error, Step(machine, 3.29).Phase);
        Assert.Equal(OverlayPhase.Hidden, Step(machine, 3.3).Phase);
    }

    [Fact]
    public void AStartFailureShowsItsError()
    {
        // The daemon sends Recording → Idle → finished: error when the microphone cannot open.
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        machine.Idle(S(0.01));
        machine.Error(S(0.01), "Microphone not found");

        Assert.Equal(OverlayPhase.Error, Step(machine, 0.05).Phase);
    }

    // Review focus 3: the daemon went away between Idle and finished.
    [Fact]
    public void IdleWithoutFinishedHidesAfterASecond()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        machine.Transcribing(S(1));
        machine.Idle(S(2));

        Assert.Equal(OverlayPhase.Transcribing, Step(machine, 2.99).Phase);
        Assert.Equal(OverlayPhase.Hidden, Step(machine, 3).Phase);
    }

    [Fact]
    public void EventsWithoutADictationShowNothing()
    {
        var machine = new OverlayStateMachine();

        machine.Transcribing(S(0));
        machine.Processing(S(0));
        machine.Done(S(0), "uk");
        machine.Cancelled(S(0));
        machine.Error(S(0), "x");

        Assert.Equal(OverlayPhase.Hidden, Step(machine, 0.1).Phase);
    }

    [Fact]
    public void AListProfileShowsItsLanguagesThenThePickedOne()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), ["uk", "en"]);
        Assert.Equal("UK/EN", Step(machine, 0.1).Chip);

        machine.Idle(S(1));
        machine.Done(S(1), "en");
        Assert.Equal("EN", Step(machine, 1.1).Chip);
    }

    [Fact]
    public void AOneLanguageProfileShowsNoChip()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), ["uk"]);
        Assert.Null(Step(machine, 0.1).Chip);

        machine.Idle(S(1));
        machine.Done(S(1), "uk");
        Assert.Null(Step(machine, 1.1).Chip);
    }

    [Fact]
    public void OnlyListeningHearsTheMicrophone()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        OverlayScene scene = OverlayScene.Hidden;
        for (var k = 1; k <= 20; k++)
        {
            scene = Step(machine, k * 0.02, Loud, AllBands);
        }

        Assert.True(scene.Level > 0.99, $"level {scene.Level}");
        Assert.All(scene.BandMeans, mean => Assert.True(mean > 0.99));

        machine.Transcribing(S(0.4));
        for (var k = 21; k <= 40; k++)
        {
            scene = Step(machine, k * 0.02, Loud, AllBands);
        }

        Assert.True(scene.Level < 0.4, $"level {scene.Level}");
    }

    [Fact]
    public void SilenceIsOnlyShownWhileListening()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        for (var k = 1; k <= 35; k++)
        {
            Step(machine, k * 0.02);
        }

        Assert.True(Step(machine, 0.72).Quiet);

        machine.Transcribing(S(0.72));
        Assert.False(Step(machine, 0.74).Quiet);
    }

    [Fact]
    public void HideEndsAnyState()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);

        machine.Hide();

        Assert.Equal(OverlayPhase.Hidden, Step(machine, 0.1).Phase);
    }

    [Fact]
    public void TheLookGoesIntoTheScene()
    {
        var machine = new OverlayStateMachine { Palette = OverlayPalette.Mist, ReducedMotion = true };
        machine.Recording(S(0), []);

        var scene = Step(machine, 0.1);

        Assert.Equal(OverlayPalette.Mist, scene.Palette);
        Assert.True(scene.ReducedMotion);
    }

    [Fact]
    public void ANewDictationRightAfterTheLastOneDoesNotFadeInAgain()
    {
        var machine = new OverlayStateMachine();
        machine.Recording(S(0), []);
        machine.Idle(S(1));
        machine.Done(S(1), null);

        machine.Recording(S(1.1), []);

        Assert.Equal(1, Step(machine, 1.12).Appear);
    }
}
