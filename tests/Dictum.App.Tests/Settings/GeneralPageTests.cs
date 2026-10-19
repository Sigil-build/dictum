using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Audio.PortAudio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.App.Tests.Settings;

public sealed class GeneralPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly FakeChannel _channel = new();
    private readonly FakeAutostart _autostart = new();
    private readonly FakeSounds _sounds = new();

    public GeneralPageTests() => Directory.CreateDirectory(_dir);

    private ConfigStore Store => new(Path.Combine(_dir, "config.json"), NullLog.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (GeneralPage Page, SettingsSession Session, FrameHub Frames) Open(GeneralSettings? general = null, bool saved = true)
    {
        if (saved)
        {
            Store.Save(new DictumConfig { General = general ?? new GeneralSettings() });
        }

        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        var frames = new FrameHub(_clock);
        var services = new GeneralServices(_channel, frames, _timers, _autostart, "/opt/dictum/dictum-gui", _sounds, _clock, NullLog.Instance);
        return (new GeneralPage(session, services), session, frames);
    }

    [Fact]
    public void TheRowsShowTheConfig()
    {
        var (page, _, _) = Open(new GeneralSettings { HistorySize = 150, Sounds = false, UiLanguage = "uk" });

        Assert.Equal(150, page.HistorySize);
        Assert.False(page.Sounds);
        Assert.Equal("uk", page.UiLanguage?.Value);
        Assert.True(page.CanEdit);
        Assert.Equal(Strings.SettingsGeneral, page.Title);
    }

    [Fact]
    public void WithoutAConfigThePageCannotEdit() => Assert.False(Open(saved: false).Page.CanEdit);

    [Fact]
    public void ShowingThePageListsTheMicrophonesWithTheDefaultFirst()
    {
        var (page, _, _) = Open();

        page.OnShown();

        Assert.Equal(["Default — HyperX SoloCast", "HyperX SoloCast", "Webcam C920"], page.Microphones.Select(m => m.Label));
        Assert.Null(page.Microphone?.Value);
    }

    [Fact]
    public void AMicrophoneThatIsGoneStaysSelected()
    {
        var (page, _, _) = Open(new GeneralSettings { AudioDevice = "Old USB mic" });

        page.OnShown();

        Assert.Equal("Old USB mic (not connected)", page.Microphones[^1].Label);
        Assert.Equal("Old USB mic", page.Microphone?.Value);
    }

    [Fact]
    public void ChoosingAMicrophoneSavesItAndRestartsTheMeterOnIt()
    {
        var (page, session, _) = Open();
        page.OnShown();
        _channel.Sent.Clear();

        page.Microphone = page.Microphones.Single(m => m.Value == "Webcam C920");

        Assert.Equal("Webcam C920", session.Config.General.AudioDevice);
        Assert.Equal([IpcCommand.MonitorStop, IpcCommand.MonitorStart], _channel.Commands);
        Assert.Equal("Webcam C920", _channel.Sent[^1].Device);
    }

    [Fact]
    public void ShowingThePageStartsTheMeterAndTheTimersRenewIt()
    {
        var (page, _, _) = Open();

        page.OnShown();
        Assert.Equal([IpcCommand.Devices, IpcCommand.MonitorStart], _channel.Commands);

        _timers.Fire();
        Assert.Equal(2, _channel.Commands.Count(c => c == IpcCommand.MonitorStart));
    }

    // Review focus 5.
    [Fact]
    public void HidingThePageStopsTheMeter()
    {
        var (page, _, _) = Open();
        page.OnShown();

        page.OnHidden();

        Assert.Equal(IpcCommand.MonitorStop, _channel.Commands.Last());
        Assert.Equal(0, _timers.Pending);
        Assert.Equal(0, page.MeterLevel);
    }

    // Review focus 3.
    [Fact]
    public void WithoutTheDaemonTheMeterSaysSo()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        var (page, _, _) = Open();

        page.OnShown();

        Assert.Equal(Strings.SettingsMeterNoDaemon, page.MeterNote);
        Assert.Equal([Strings.SettingsMicrophoneSystemDefault], page.Microphones.Select(m => m.Label));
    }

    [Fact]
    public void TheDaemonComingBackStartsTheMeter()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        var (page, _, _) = Open();
        page.OnShown();

        _channel.Link = FakeChannel.Connected();
        page.OnLink(_channel.Link);

        Assert.Null(page.MeterNote);
        Assert.Equal(IpcCommand.MonitorStart, _channel.Commands.Last());
        Assert.Equal(3, page.Microphones.Count);
    }

    [Fact]
    public void ARefusedMeterShowsTheDaemonsReason()
    {
        _channel.Answer = request => request.Command == IpcCommand.MonitorStart
            ? IpcResponse.Failure("A dictation is running; the level meter can start when it has finished.")
            : new IpcResponse(true);
        var (page, _, _) = Open();

        page.OnShown();

        Assert.Equal("A dictation is running; the level meter can start when it has finished.", page.MeterNote);
    }

    [Fact]
    public void TheMeterFollowsTheFrames()
    {
        var (page, _, frames) = Open();
        page.OnShown();

        for (var i = 0; i < 40; i++)
        {
            frames.Publish(new IpcEvent(IpcEventKind.Frame, Rms: (float)Math.Pow(10, -31 / 20.0)));
            _clock.Advance(GeneralPage.MeterTick);
            _timers.Fire();
        }

        Assert.Equal(0.58, page.MeterLevel, 2);
        Assert.Equal("−31 dBFS", page.MeterText);
    }

    [Fact]
    public void TheToggleWritesAndRemovesTheLoginItem()
    {
        var (page, _, _) = Open();

        page.StartAtLogin = true;
        Assert.Equal(new LoginItem(LoginApp.DesktopApp, "/opt/dictum/dictum-gui"), _autostart.Item);
        Assert.Equal(LoginApp.DesktopApp, _autostart.Installed);

        page.StartAtLogin = false;
        Assert.Null(_autostart.Installed);
    }

    [Fact]
    public void ADaemonLoginItemShowsAsOff()
    {
        _autostart.Installed = LoginApp.Daemon;

        Assert.False(Open().Page.StartAtLogin);
    }

    [Fact]
    public void ALoginItemThatFailsIsShownAndTheToggleGoesBack()
    {
        _autostart.Fails = new UnauthorizedAccessException("Access denied.");
        var (page, _, _) = Open();

        page.StartAtLogin = true;

        Assert.False(page.StartAtLogin);
        Assert.Contains("Access denied.", page.StartAtLoginError);
    }

    // Review focus 1.
    [Fact]
    public void AnEmptyHistoryFieldIsNotSaved()
    {
        var (page, _, _) = Open(new GeneralSettings { HistorySize = 100 });

        page.HistorySize = null;

        Assert.Equal(Strings.SettingsNumberRequired, page.HistoryError);
        Assert.Equal(0, _timers.Pending);
        Assert.Equal(100, Store.Load().General.HistorySize);
    }

    [Fact]
    public void AHistorySizeIsSavedAfterTheDelay()
    {
        var (page, _, _) = Open(new GeneralSettings { HistorySize = 100 });

        page.HistorySize = 300;
        _timers.Fire();

        Assert.Null(page.HistoryError);
        Assert.Equal(300, Store.Load().General.HistorySize);
    }

    [Fact]
    public void SoundsAndLanguageAreSaved()
    {
        var (page, session, _) = Open();

        page.Sounds = false;
        page.UiLanguage = page.UiLanguages.Single(l => l.Value == "uk");
        session.Flush();

        var saved = Store.Load().General;
        Assert.False(saved.Sounds);
        Assert.Equal("uk", saved.UiLanguage);
    }

    [Fact]
    public async Task PreviewPlaysStartThenStop()
    {
        var (page, _, _) = Open();

        await page.PreviewCommand.ExecuteAsync(null);

        Assert.Equal([SoundCue.Start, SoundCue.Stop], _sounds.Played);
    }

    [Fact]
    public void AnOutsideEditUpdatesTheRows()
    {
        var (page, session, _) = Open(new GeneralSettings { HistorySize = 100 });

        session.OnFileChanged(new DictumConfig { General = new GeneralSettings { HistorySize = 77, Sounds = false } });

        Assert.Equal(77, page.HistorySize);
        Assert.False(page.Sounds);
        Assert.Equal(0, _timers.Pending);
    }


    // Final review C1: backspacing "100" passes through 10 and 1; the empty field must leave the file alone.
    [Fact]
    public void BackspacingToAnEmptyFieldSavesNothing()
    {
        var (page, session, _) = Open(new GeneralSettings { HistorySize = 100 });

        page.HistorySize = 10;
        page.HistorySize = 1;
        page.HistorySize = null;
        session.Flush();

        Assert.Equal(0, _timers.Pending);
        Assert.Equal(100, Store.Load().General.HistorySize);
        Assert.Null(page.HistorySize);
        Assert.Equal(Strings.SettingsNumberRequired, page.HistoryError);
    }

    // Final review C1: another row's save must not refill the field under the cursor.
    [Fact]
    public void AnEmptyFieldStaysEmptyWhileAnotherRowSaves()
    {
        var (page, _, _) = Open(new GeneralSettings { HistorySize = 100 });
        page.HistorySize = null;

        page.Sounds = false;
        _timers.Fire();

        Assert.Null(page.HistorySize);
        Assert.Equal(Strings.SettingsNumberRequired, page.HistoryError);
        var saved = Store.Load().General;
        Assert.Equal((100, false), (saved.HistorySize, saved.Sounds));
    }


    // Final review I3: a command that throws would crash dictum-gui.
    [Fact]
    public async Task APreviewThatFailsDoesNotThrow()
    {
        _sounds.Fails = new InvalidOperationException("No output device.");
        var (page, _, _) = Open();

        await page.PreviewCommand.ExecuteAsync(null);

        Assert.Empty(_sounds.Played);
    }


    // Final review M5: a switch while a start is still out must stop that monitor before starting the new one.
    [Fact]
    public void SwitchingWhileAStartIsOutStopsItFirst()
    {
        var start = new TaskCompletionSource<IpcResponse>();
        _channel.Held[IpcCommand.MonitorStart] = start;
        var (page, _, _) = Open();
        page.OnShown();

        page.Microphone = page.Microphones.Single(m => m.Value == "Webcam C920");
        Assert.Equal([IpcCommand.Devices, IpcCommand.MonitorStart], _channel.Commands);

        start.SetResult(new IpcResponse(true));

        Assert.Equal([IpcCommand.Devices, IpcCommand.MonitorStart, IpcCommand.MonitorStop, IpcCommand.MonitorStart], _channel.Commands);
        Assert.Equal("Webcam C920", _channel.Sent[^1].Device);
    }

    // Final review M5: each request opens its own connection, so the start waits for the stop's answer.
    [Fact]
    public void TheNewStartWaitsForTheStop()
    {
        var (page, _, _) = Open();
        page.OnShown();
        var stop = new TaskCompletionSource<IpcResponse>();
        _channel.Held[IpcCommand.MonitorStop] = stop;
        _channel.Sent.Clear();

        page.Microphone = page.Microphones.Single(m => m.Value == "Webcam C920");
        Assert.Equal([IpcCommand.MonitorStop], _channel.Commands);

        stop.SetResult(new IpcResponse(true));
        Assert.Equal([IpcCommand.MonitorStop, IpcCommand.MonitorStart], _channel.Commands);
    }

    // Final review M5: hidden while a start is out, the monitor it opened is closed once the answer comes.
    [Fact]
    public void HidingWhileAStartIsOutClosesTheMonitorItOpened()
    {
        var start = new TaskCompletionSource<IpcResponse>();
        _channel.Held[IpcCommand.MonitorStart] = start;
        var (page, _, _) = Open();
        page.OnShown();

        page.OnHidden();
        start.SetResult(new IpcResponse(true));

        Assert.Equal(IpcCommand.MonitorStop, _channel.Commands.Last());
    }

    // Final review M6: without a device list nothing is known to be missing.
    [Fact]
    public void WhileTheDaemonIsDownTheConfiguredMicrophoneIsNotCalledMissing()
    {
        _channel.Link = new DaemonLink(DaemonLinkState.Restarting);
        var (page, _, _) = Open(new GeneralSettings { AudioDevice = "Old USB mic" });

        page.OnShown();

        Assert.Equal("Old USB mic", page.Microphones[^1].Label);
        Assert.Equal("Old USB mic", page.Microphone?.Value);
    }

    // Spec 7: General → "Run setup again".
    [Fact]
    public void RunSetupAgainAsksTheShell()
    {
        var (page, _, _) = Open();
        var asked = 0;
        page.SetupRequested += () => asked++;

        page.RunSetupCommand.Execute(null);

        Assert.Equal(1, asked);
    }
}
