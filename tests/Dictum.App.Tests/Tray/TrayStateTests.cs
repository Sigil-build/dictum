using Dictum.App.Daemon;
using Dictum.App.Tray;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Tray;

public class TrayStateTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IdleShowsTheSelectedProfileAndItsHotkey()
    {
        var tray = Connected(Status(selected: "en"));

        var view = tray.View();

        Assert.Equal(TrayGlyph.Idle, view.Glyph);
        Assert.Equal("Dictum · en · Ctrl+Alt+E", view.Tooltip);
        Assert.False(view.Attention);
        Assert.Equal(TrayCommand.Settings, view.Activate);
        var profiles = view.Menu.Where(i => i.Command == TrayCommand.UseProfile).ToList();
        Assert.Equal(["uk", "en"], profiles.Select(p => p.Profile));
        Assert.Equal([false, true], profiles.Select(p => p.Checked));
    }

    [Fact]
    public void WithoutASelectionTheFirstProfileIsShown()
    {
        var view = Connected(Status()).View();

        Assert.Equal("Dictum · uk · Ctrl+Alt+Space", view.Tooltip);
        Assert.True(view.Menu.Single(i => i.Profile == "uk").Checked);
    }

    [Fact]
    public void TheMenuFollowsTheDesignOrder()
    {
        var view = Connected(Status()).View();

        Assert.Equal(
            ["Profile", "uk", "en", "", "Pause all hotkeys", "History", "Settings", "", "Quit Dictum"],
            view.Menu.Select(i => i.Label));
        Assert.Equal(TrayMenuItemKind.Header, view.Menu[0].Kind);
    }

    [Theory]
    [InlineData(nameof(PipelineState.Recording), TrayGlyph.Recording, "Dictum · Recording", true)]
    [InlineData(nameof(PipelineState.Transcribing), TrayGlyph.Processing, "Dictum · Transcribing…", false)]
    [InlineData(nameof(PipelineState.Processing), TrayGlyph.Processing, "Dictum · Transcribing…", false)]
    [InlineData(nameof(PipelineState.Output), TrayGlyph.Processing, "Dictum · Transcribing…", false)]
    [InlineData(nameof(PipelineState.Idle), TrayGlyph.Idle, "Dictum · uk · Ctrl+Alt+Space", false)]
    public void StateEventsSetTheGlyph(string state, TrayGlyph glyph, string tooltip, bool attention)
    {
        var tray = Connected(Status());

        tray.OnEvent(new IpcEvent(IpcEventKind.State, State: state, Profile: "uk"));
        var view = tray.View();

        Assert.Equal((glyph, tooltip, attention), (view.Glyph, view.Tooltip, view.Attention));
    }

    [Fact]
    public void AnErrorHoldsForThirtySeconds()
    {
        var tray = Connected(Status());
        tray.OnEvent(IpcEvent.Finished(DictationOutcome.Error, "Microphone not found", null, 0, null));

        var shown = tray.View();
        _clock.Advance(TimeSpan.FromSeconds(29));
        var stillShown = tray.View();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var gone = tray.View();

        Assert.Equal((TrayGlyph.Error, "Dictum · Microphone not found", true), (shown.Glyph, shown.Tooltip, shown.Attention));
        Assert.Equal(TrayGlyph.Error, stillShown.Glyph);
        Assert.Equal(TrayGlyph.Idle, gone.Glyph);
    }

    [Fact]
    public void ASuccessfulDictationClearsTheError()
    {
        var tray = Connected(Status());
        tray.OnEvent(IpcEvent.Finished(DictationOutcome.Error, "Microphone not found", null, 0, null));

        tray.OnEvent(IpcEvent.Finished(DictationOutcome.Pasted, null, "uk", 1200, 800));

        Assert.Equal(TrayGlyph.Idle, tray.View().Glyph);
        Assert.Null(tray.NextChange);
    }

    [Fact]
    public void NextChangeIsWhenTheErrorExpires()
    {
        var tray = Connected(Status());
        tray.OnEvent(IpcEvent.Finished(DictationOutcome.Error, "boom", null, 0, null));

        Assert.Equal(_clock.GetUtcNow() + TrayState.ErrorHold, tray.NextChange);
    }

    [Fact]
    public void RecordingWinsOverAHeldError()
    {
        var tray = Connected(Status());
        tray.OnEvent(IpcEvent.Finished(DictationOutcome.Error, "boom", null, 0, null));

        tray.OnEvent(new IpcEvent(IpcEventKind.State, State: nameof(PipelineState.Recording)));

        Assert.Equal(TrayGlyph.Recording, tray.View().Glyph);
    }

    [Fact]
    public void PausedHotkeysShowThePauseGlyphAndAResumeItem()
    {
        var view = Connected(Status(paused: true)).View();

        Assert.Equal((TrayGlyph.Paused, "Dictum · Hotkeys paused"), (view.Glyph, view.Tooltip));
        Assert.Contains(view.Menu, i => i.Command == TrayCommand.ResumeHotkeys && i.Label == "Resume hotkeys");
        Assert.DoesNotContain(view.Menu, i => i.Command == TrayCommand.PauseHotkeys);
    }

    [Theory]
    [InlineData(DaemonLinkState.Failed, "Dictum · Daemon not running — click to restart")]
    [InlineData(DaemonLinkState.VersionMismatch, "Dictum · The daemon is from another version — click to restart it")]
    public void ADownDaemonOffersARestart(DaemonLinkState state, string tooltip)
    {
        var tray = new TrayState(_clock);
        tray.OnLink(new DaemonLink(state, Problem: "details"));

        var view = tray.View();

        Assert.Equal((TrayGlyph.Error, tooltip, true), (view.Glyph, view.Tooltip, view.Attention));
        Assert.Equal(TrayCommand.RestartDaemon, view.Activate);
        Assert.Equal(TrayCommand.RestartDaemon, view.Menu[0].Command);
        Assert.DoesNotContain(view.Menu, i => i.Command is TrayCommand.UseProfile or TrayCommand.PauseHotkeys);
    }

    [Theory]
    [InlineData(DaemonLinkState.NotConfigured, TrayGlyph.Idle, "Dictum · Not set up yet")]
    [InlineData(DaemonLinkState.Starting, TrayGlyph.Processing, "Dictum · Starting…")]
    [InlineData(DaemonLinkState.Restarting, TrayGlyph.Error, "Dictum · Daemon not running — restarting…")]
    public void OtherLinkStatesHaveTheirOwnTooltip(DaemonLinkState state, TrayGlyph glyph, string tooltip)
    {
        var tray = new TrayState(_clock);
        tray.OnLink(new DaemonLink(state));

        var view = tray.View();

        Assert.Equal((glyph, tooltip), (view.Glyph, view.Tooltip));
        Assert.Equal(["History", "Settings", "", "Quit Dictum"], view.Menu.Select(i => i.Label));
    }

    [Fact]
    public void ALostLinkForgetsTheRecordingState()
    {
        var tray = Connected(Status());
        tray.OnEvent(new IpcEvent(IpcEventKind.State, State: nameof(PipelineState.Recording)));

        tray.OnLink(new DaemonLink(DaemonLinkState.Restarting));
        tray.OnLink(new DaemonLink(DaemonLinkState.Connected, Status()));

        Assert.Equal(TrayGlyph.Idle, tray.View().Glyph);
    }

    [Fact]
    public void LongErrorsAreShortenedForTheTooltip()
    {
        var tray = Connected(Status());
        tray.OnEvent(IpcEvent.Finished(DictationOutcome.Error, new string('x', 300), null, 0, null));

        var tooltip = tray.View().Tooltip;

        Assert.Equal(TrayState.MaxTooltip, tooltip.Length);
        Assert.EndsWith("…", tooltip);
    }

    [Fact]
    public void DownloadProgressIsAddedToTheTooltip()
    {
        var tray = Connected(Status());

        tray.OnDownloads(0.45);
        var downloading = tray.View().Tooltip;
        tray.OnDownloads(null);

        Assert.Equal("Dictum · uk · Ctrl+Alt+Space · downloading models, 45%", downloading);
        Assert.Equal("Dictum · uk · Ctrl+Alt+Space", tray.View().Tooltip);
    }

    [Fact]
    public void ProfileItemsCarryTheirLanguagesFromTheConfig()
    {
        var tray = Connected(Status());
        var config = new DictumConfig
        {
            Profiles =
            [
                new ProfileConfig { Id = "uk", Stt = "local", Language = "uk,en" },
                new ProfileConfig { Id = "en", Stt = "local" },
            ],
        };

        tray.OnConfig(config);
        var items = tray.View().Menu.Where(i => i.Command == TrayCommand.UseProfile).ToList();

        Assert.Equal(["uk, en", "auto"], items.Select(i => i.Detail));
    }

    private TrayState Connected(DaemonStatus status)
    {
        var tray = new TrayState(_clock);
        tray.OnLink(new DaemonLink(DaemonLinkState.Connected, status));
        return tray;
    }

    private static DaemonStatus Status(string? selected = null, bool paused = false) => new(
        nameof(PipelineState.Idle),
        null,
        [new ProfileStatus("uk", "Ctrl+Alt+Space", "PushToTalk", true), new ProfileStatus("en", "Ctrl+Alt+E", "Toggle", true)],
        "test hotkeys",
        "test paste",
        42,
        Paused: paused,
        SelectedProfile: selected);
}
