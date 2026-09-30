using Dictum.Audio.PortAudio;
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
    public async Task UnsupportedPlatformStillServesIpc()
    {
        var platform = UnsupportedPlatform.Create("macOS", "Not yet.");
        await using var daemon = new DictumDaemon(Config(), c => new FakeEngine(c, _stt), platform, _sounds, NullLog.Instance);
        await daemon.StartAsync(_ct);

        Assert.Equal("Not yet.", daemon.Status().Warning);
        Assert.True((await daemon.HandleAsync(new IpcRequest("status"), _ct)).Ok);
    }

    private async Task<DictumDaemon> StartAsync(DictumConfig config, bool withHotkeys = true)
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
        }, platform, _sounds, NullLog.Instance);
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
