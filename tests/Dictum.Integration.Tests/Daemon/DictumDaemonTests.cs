using Dictum.Audio.PortAudio;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Platform.Hotkeys;

namespace Dictum.Integration.Tests.Daemon;

public class DictumDaemonTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeHotkeys _hotkeys = new();
    private readonly FakeInjector _injector = new();
    private readonly FakeSounds _sounds = new();
    private readonly FakeNotifier _notifier = new();
    private readonly EchoStt _stt = new("привіт світ");
    private readonly List<FakeEngine> _engines = [];
    private readonly IpcEventHub _events = new();

    [Fact]
    public async Task PushToTalkRecordsWhileHeldAndPastesOnRelease()
    {
        await using var daemon = await StartAsync(Config());

        _hotkeys.Raise("uk", HotkeyPhase.Pressed);
        await WaitForAsync(() => daemon.State == PipelineState.Recording);
        _hotkeys.Raise("uk", HotkeyPhase.Released);
        await WaitForAsync(() => _injector.Injected.Count == 1 && daemon.State == PipelineState.Idle);

        var (text, options) = _injector.Injected[0];
        Assert.Equal("привіт світ", text);
        Assert.Equal(PasteShortcut.CtrlShiftV, options.Shortcut);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.RestoreDelay);
        Assert.Equal([SoundCue.Start, SoundCue.Stop], _sounds.Played);
    }

    [Fact]
    public async Task ToggleStartsOnFirstPressAndStopsOnSecond()
    {
        await using var daemon = await StartAsync(Config());

        _hotkeys.Raise("en", HotkeyPhase.Pressed);
        _hotkeys.Raise("en", HotkeyPhase.Released);
        await WaitForAsync(() => daemon.State == PipelineState.Recording);
        Assert.Empty(_injector.Injected);

        _hotkeys.Raise("en", HotkeyPhase.Pressed);
        await WaitForAsync(() => _injector.Injected.Count == 1);
    }

    [Fact]
    public async Task IpcToggleWorksWithoutHotkeys()
    {
        await using var daemon = await StartAsync(Config(), withHotkeys: false);

        var started = await daemon.HandleAsync(new IpcRequest("toggle", "en"), _ct);
        Assert.Equal("Recording", started.Status!.State);
        Assert.Equal("en", started.Status.ActiveProfile);

        await daemon.HandleAsync(new IpcRequest("toggle"), _ct);
        await WaitForAsync(() => _injector.Injected.Count == 1);
        Assert.StartsWith("none", daemon.Status().HotkeyBackend);
    }

    [Fact]
    public async Task CancelDiscardsTheRecording()
    {
        await using var daemon = await StartAsync(Config());
        await daemon.HandleAsync(new IpcRequest("start"), _ct);

        var response = await daemon.HandleAsync(new IpcRequest("cancel"), _ct);

        Assert.Equal("Idle", response.Status!.State);
        Assert.Empty(_injector.Injected);
    }

    [Fact]
    public async Task CancelDuringTranscriptionDropsTheResult()
    {
        _stt.Gate = new TaskCompletionSource();
        await using var daemon = await StartAsync(Config());
        await daemon.HandleAsync(new IpcRequest("start"), _ct);
        await daemon.HandleAsync(new IpcRequest("stop"), _ct);
        await WaitForAsync(() => daemon.State == PipelineState.Transcribing);

        await daemon.HandleAsync(new IpcRequest("cancel"), _ct);
        await WaitForAsync(() => daemon.State == PipelineState.Idle);

        Assert.Empty(_injector.Injected);
    }

    [Fact]
    public async Task PasteFailurePlaysErrorAndNotifies()
    {
        _injector.FailWith = new InvalidOperationException("no focused window");
        await using var daemon = await StartAsync(Config());

        _hotkeys.Raise("uk", HotkeyPhase.Pressed);
        await WaitForAsync(() => daemon.State == PipelineState.Recording);
        _hotkeys.Raise("uk", HotkeyPhase.Released);
        await WaitForAsync(() => _notifier.Shown.Count == 1);

        Assert.Contains(SoundCue.Error, _sounds.Played);
        Assert.Contains("no focused window", _notifier.Shown[0]);
        Assert.Equal(PipelineState.Idle, daemon.State);
    }

    [Fact]
    public async Task HotkeysAreRegisteredPerProfile()
    {
        await using var daemon = await StartAsync(Config());

        Assert.Equal(["uk", "en"], _hotkeys.Bindings.Select(b => b.Id));
        Assert.Equal("Ctrl+Alt+Space", _hotkeys.Bindings[0].Hotkey.ToString());
        Assert.All(daemon.Status().Profiles, p => Assert.True(p.Registered));
    }

    [Fact]
    public async Task RegistrationFailureIsReportedButTheDaemonKeepsRunning()
    {
        _hotkeys.FailWith = new HotkeyUnavailableException("Portal missing.", "Bind 'dictum toggle' in your compositor.");

        await using var daemon = await StartAsync(Config());

        var status = daemon.Status();
        Assert.Contains("dictum toggle", status.Warning);
        Assert.All(status.Profiles, p => Assert.False(p.Registered));
        Assert.Single(_notifier.Shown);
    }

    [Fact]
    public async Task ReloadSwapsTheEngineAndReRegistersHotkeys()
    {
        await using var daemon = await StartAsync(Config());
        var changed = Config() with
        {
            Profiles = [new ProfileConfig { Id = "only", Stt = "s", Hotkey = "Ctrl+F9", Mode = HotkeyMode.Toggle }],
        };

        await daemon.ApplyConfigAsync(changed, _ct);

        Assert.True(_engines[0].Disposed);
        Assert.Equal(["only"], _hotkeys.Bindings.Select(b => b.Id));
    }

    [Fact]
    public async Task FailedPlatformSetupIsReportedAndTheDaemonKeepsRunning()
    {
        var platform = new PlatformServices(
            "test",
            () => _hotkeys,
            _injector,
            new UnavailableSecretStore(),
            null,
            _notifier,
            _ => Task.FromResult<IReadOnlyList<PlatformCheck>>([]),
            PrepareAsync: _ => throw new Dictum.Platform.Injection.InjectionUnavailableException("Keyboard control was not allowed."));
        await using var daemon = new DictumDaemon(Config(), c => new FakeEngine(c, _stt), platform, _sounds, NullLog.Instance);

        await daemon.StartAsync(_ct);
        await WaitForAsync(() => _notifier.Shown.Count == 1);

        Assert.Contains("not allowed", _notifier.Shown[0]);
        Assert.True((await daemon.HandleAsync(new IpcRequest("status"), _ct)).Ok);
    }

    [Fact]
    public async Task UnsupportedPlatformStillServesIpc()
    {
        var platform = UnsupportedPlatform.Create("macOS", "Not yet.");
        await using var daemon = new DictumDaemon(Config(), c => new FakeEngine(c, _stt), platform, _sounds, NullLog.Instance);
        await daemon.StartAsync(_ct);

        Assert.Equal("Not yet.", daemon.Status().Warning);
        Assert.True((await daemon.HandleAsync(new IpcRequest("status"), _ct)).Ok);
    }

    [Fact]
    public async Task ADictationPublishesItsStatesAndOneFinishedEvent()
    {
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();

        _hotkeys.Raise("uk", HotkeyPhase.Pressed);
        await WaitForAsync(() => daemon.State == PipelineState.Recording);
        _hotkeys.Raise("uk", HotkeyPhase.Released);
        var events = await CollectAsync(subscriber, e => e.Any(x => x.Event == "finished"));

        var recording = events.First(e => e is { Event: "state", State: "Recording" });
        Assert.Equal(("uk", "PushToTalk"), (recording.Profile, recording.Mode));
        Assert.Contains(events, e => e is { Event: "state", State: "Transcribing" });
        var finished = Assert.Single(events, e => e.Event == "finished");
        Assert.Equal((DictationOutcome.Pasted, "uk"), (finished.Outcome, finished.Language));
    }

    [Fact]
    public async Task ClipboardOutputFinishesAsCopiedAndEmptyTextAsEmpty()
    {
        var config = Config();
        config.Profiles[0] = config.Profiles[0] with { Output = OutputMode.Clipboard };
        await using var daemon = await StartAsync(config);
        using var subscriber = _events.Subscribe();

        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);
        await daemon.HandleAsync(new IpcRequest("stop"), _ct);
        var first = await CollectAsync(subscriber, e => e.Any(x => x.Event == "finished"));
        _stt.Text = "";
        await WaitForAsync(() => daemon.State == PipelineState.Idle);
        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);
        await daemon.HandleAsync(new IpcRequest("stop"), _ct);
        var second = await CollectAsync(subscriber, e => e.Any(x => x.Event == "finished"));

        Assert.Equal(DictationOutcome.Copied, first.Single(e => e.Event == "finished").Outcome);
        Assert.Equal(DictationOutcome.Empty, second.Single(e => e.Event == "finished").Outcome);
    }

    [Fact]
    public async Task AFailedPasteFinishesAsAnErrorWithTheMessage()
    {
        _injector.FailWith = new InvalidOperationException("no paste here");
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();

        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);
        await daemon.HandleAsync(new IpcRequest("stop"), _ct);
        var finished = (await CollectAsync(subscriber, e => e.Any(x => x.Event == "finished"))).Single(e => e.Event == "finished");

        Assert.Equal(DictationOutcome.Error, finished.Outcome);
        Assert.Contains("no paste here", finished.Message);
    }

    [Fact]
    public async Task CancelWhileRecordingFinishesOnce()
    {
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();

        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);
        var stop = daemon.HandleAsync(new IpcRequest("stop"), _ct);
        await daemon.HandleAsync(new IpcRequest("cancel"), _ct);
        await stop;
        var events = await CollectAsync(subscriber, e => e.Any(x => x.Event == "finished"));
        await Task.Delay(200, _ct);
        events.AddRange(await DrainAsync(subscriber));

        Assert.Single(events, e => e.Event == "finished");
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task FramesArePublishedOnlyWhileRecording()
    {
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();
        var audio = _engines[^1].Audio;

        audio.Raise(new AudioFrame(0.2f, new float[24], TimeSpan.FromSeconds(1)));
        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);
        audio.Raise(new AudioFrame(0.3f, new float[24], TimeSpan.FromSeconds(2)));
        var events = await CollectAsync(subscriber, e => e.Any(x => x is { Event: "frame", Rms: 0.3f }));

        Assert.DoesNotContain(events, e => e is { Event: "frame", Rms: 0.2f });
    }

    [Fact]
    public async Task ReloadMovesFrameEventsToTheNewEngine()
    {
        await using var daemon = await StartAsync(Config());
        await daemon.ApplyConfigAsync(Config(), _ct);
        using var subscriber = _events.Subscribe();
        var (old, current) = (_engines[0].Audio, _engines[1].Audio);

        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);
        old.Raise(new AudioFrame(0.4f, new float[24], TimeSpan.FromSeconds(1)));
        current.Raise(new AudioFrame(0.5f, new float[24], TimeSpan.FromSeconds(1)));
        var events = await CollectAsync(subscriber, e => e.Any(x => x is { Event: "frame", Rms: 0.5f }));

        Assert.DoesNotContain(events, e => e is { Event: "frame", Rms: 0.4f });
    }

    [Fact]
    public async Task ConfigWarningsReachTheStatusAndAStatusEvent()
    {
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();

        await daemon.ApplyConfigAsync(Config(), _ct, [new ConfigIssue(ConfigIssueSeverity.Warning, "profiles[0].language", "sounds alike")]);
        var events = await CollectAsync(subscriber, e => e.Any(x => x.Event == "status"));

        Assert.Equal(["profiles[0].language: sounds alike"], daemon.Status().ConfigWarnings!);
        Assert.Equal(daemon.Status().ConfigWarnings, events.Last(e => e.Event == "status").Status!.ConfigWarnings);
    }

    [Fact]
    public async Task SubscribeIsAnsweredWithTheProtocolVersion()
    {
        await using var daemon = await StartAsync(Config());

        var response = await daemon.HandleAsync(new IpcRequest("subscribe"), _ct);

        Assert.Equal((true, IpcProtocol.Version), (response.Ok, response.Status!.Protocol));
    }

    private static async Task<List<IpcEvent>> DrainAsync(IpcSubscriber subscriber)
    {
        using var quick = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            return [.. await subscriber.NextAsync(quick.Token)];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    [Fact]
    public async Task ShutdownCancelsTheDictationAndCompletesShutdownRequested()
    {
        await using var daemon = await StartAsync(Config());
        await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);

        Assert.True((await daemon.HandleAsync(new IpcRequest("shutdown"), _ct)).Ok);

        await daemon.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.Equal(PipelineState.Idle, daemon.State);
    }

    [Fact]
    public async Task PauseUnregistersTheHotkeysAndResumeRestoresThem()
    {
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();

        var paused = await daemon.HandleAsync(new IpcRequest("pause-hotkeys"), _ct);
        Assert.Empty(_hotkeys.Bindings);
        Assert.True(paused.Status!.Paused);
        Assert.Contains(await CollectAsync(subscriber, e => e.Any(x => x.Event == "status")), e => e.Status!.Paused);

        await daemon.HandleAsync(new IpcRequest("resume-hotkeys"), _ct);
        Assert.Equal(2, _hotkeys.Bindings.Count);
        Assert.False(daemon.Status().Paused);
    }

    [Fact]
    public async Task ReloadWhilePausedKeepsHotkeysOff()
    {
        await using var daemon = await StartAsync(Config());
        await daemon.HandleAsync(new IpcRequest("pause-hotkeys"), _ct);

        await daemon.ApplyConfigAsync(Config(), _ct);

        Assert.Empty(_hotkeys.Bindings);
        Assert.True(daemon.Status().Paused);
    }

    [Fact]
    public async Task UseProfileMakesItTheDefault()
    {
        await using var daemon = await StartAsync(Config(), withHotkeys: false);

        await daemon.HandleAsync(new IpcRequest("use-profile", "en"), _ct);
        var started = await daemon.HandleAsync(new IpcRequest("toggle"), _ct);

        Assert.Equal(("en", "en"), (started.Status!.ActiveProfile, started.Status.SelectedProfile));
        Assert.False((await daemon.HandleAsync(new IpcRequest("use-profile", "nope"), _ct)).Ok);
    }

    [Fact]
    public async Task ReloadRunsTheHandlerAndFailsWithoutOne()
    {
        await using var daemon = await StartAsync(Config());
        Assert.False((await daemon.HandleAsync(new IpcRequest("reload"), _ct)).Ok);

        var reloaded = false;
        daemon.ReloadHandler = _ =>
        {
            reloaded = true;
            return Task.CompletedTask;
        };

        Assert.True((await daemon.HandleAsync(new IpcRequest("reload"), _ct)).Ok);
        Assert.True(reloaded);
    }

    [Fact]
    public async Task DevicesListsTheMicrophones()
    {
        await using var daemon = await StartAsync(Config());
        _engines[^1].Audio.Devices = [new AudioDevice("3", "Arctis Nova 5", "WASAPI", 1, 48_000, IsDefault: true)];

        var response = await daemon.HandleAsync(new IpcRequest("devices"), _ct);

        Assert.Equal("Arctis Nova 5", Assert.Single(response.Devices!).Name);
    }

    [Fact]
    public async Task AMicrophoneFailureOnStartIsAFailureAnswer()
    {
        await using var daemon = await StartAsync(Config());
        _engines[^1].Audio.FailWith = new AudioDeviceException("Microphone not found.", "Run 'dictum devices'.");

        var response = await daemon.HandleAsync(new IpcRequest("start", "uk"), _ct);

        Assert.False(response.Ok);
        Assert.Contains("Microphone not found", response.Error);
    }

    [Fact]
    public async Task MonitorPublishesFramesWhileTheDaemonStaysIdle()
    {
        await using var daemon = await StartAsync(Config());
        using var subscriber = _events.Subscribe();
        var audio = _engines[^1].Audio;

        Assert.True((await Send(daemon, "monitor-start")).Ok);
        audio.Raise(new AudioFrame(0.25f, new float[24], TimeSpan.FromSeconds(1)));
        await CollectAsync(subscriber, e => e.Any(x => x is { Event: "frame", Rms: 0.25f }));

        Assert.Equal((1, PipelineState.Idle), (audio.Starts, daemon.State));
        await Send(daemon, "monitor-stop");
        Assert.Equal(1, audio.Stops);
    }

    [Fact]
    public async Task MonitorExpiresWithoutRenewal()
    {
        await using var daemon = await StartAsync(Config(), monitorLifetime: TimeSpan.FromMilliseconds(200));

        await Send(daemon, "monitor-start");

        await WaitForAsync(() => _engines[^1].Audio.Stops == 1);
    }

    [Fact]
    public async Task RenewingKeepsTheMonitorOpen()
    {
        await using var daemon = await StartAsync(Config(), monitorLifetime: TimeSpan.FromMilliseconds(400));

        for (var i = 0; i < 6; i++)
        {
            await Send(daemon, "monitor-start");
            await Task.Delay(100, _ct);
        }

        Assert.Equal((1, 0), (_engines[^1].Audio.Starts, _engines[^1].Audio.Stops));
    }

    [Fact]
    public async Task MonitorIsRefusedDuringADictation()
    {
        await using var daemon = await StartAsync(Config());
        await Send(daemon, "start", "uk");

        Assert.False((await Send(daemon, "monitor-start")).Ok);
    }

    [Fact]
    public async Task DictationStopsARunningMonitor()
    {
        await using var daemon = await StartAsync(Config());
        await Send(daemon, "monitor-start");

        await Send(daemon, "start", "uk");

        var audio = _engines[^1].Audio;
        Assert.Equal((2, 1, PipelineState.Recording), (audio.Starts, audio.Stops, daemon.State));
    }

    private Task<IpcResponse> Send(DictumDaemon daemon, string command, string? profile = null) =>
        daemon.HandleAsync(new IpcRequest(command, profile), _ct);

    private async Task<List<IpcEvent>> CollectAsync(IpcSubscriber subscriber, Func<List<IpcEvent>, bool> until)
    {
        var events = new List<IpcEvent>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!until(events))
        {
            events.AddRange(await subscriber.NextAsync(timeout.Token));
        }

        return events;
    }

    private async Task<DictumDaemon> StartAsync(DictumConfig config, bool withHotkeys = true, TimeSpan? monitorLifetime = null)
    {
        var platform = new PlatformServices(
            "test",
            withHotkeys ? () => _hotkeys : null,
            _injector,
            new UnavailableSecretStore(),
            null,
            _notifier,
            _ => Task.FromResult<IReadOnlyList<PlatformCheck>>([]));
        var daemon = new DictumDaemon(config, c =>
        {
            var engine = new FakeEngine(c, _stt);
            _engines.Add(engine);
            return engine;
        }, platform, _sounds, NullLog.Instance, _events, monitorLifetime: monitorLifetime);
        await daemon.StartAsync(_ct);
        return daemon;
    }

    private static DictumConfig Config() => new()
    {
        General = new GeneralSettings { PasteDelayMs = 250 },
        Stt = new() { ["s"] = new WhisperLocalSttConfig { Model = "m.bin" } },
        Profiles =
        [
            new ProfileConfig { Id = "uk", Stt = "s", Language = "uk", Hotkey = "Ctrl+Alt+Space", PasteShortcut = PasteShortcut.CtrlShiftV },
            new ProfileConfig { Id = "en", Stt = "s", Language = "en", Hotkey = "Ctrl+Alt+E", Mode = HotkeyMode.Toggle },
        ],
    };

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the daemon");
            await Task.Delay(10, _ct);
        }
    }
}
