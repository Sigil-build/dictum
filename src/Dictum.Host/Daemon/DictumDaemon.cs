using Dictum.Audio.PortAudio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;

namespace Dictum.Host.Daemon;

/// <summary>
/// Hotkeys and IPC commands drive one dictation at a time: push-to-talk records while the combination
/// is held, toggle starts on one press and stops on the next. The result goes to the focused app.
/// </summary>
public sealed class DictumDaemon : IAsyncDisposable
{
    private readonly Func<DictumConfig, IDictationEngine> _createEngine;
    private readonly PlatformServices _platform;
    private readonly ISoundPlayer _sounds;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private IHotkeyService? _hotkeys;
    private IDictationEngine _engine;
    private string? _activeProfile;
    private CancellationTokenSource? _dictation;
    private bool _stopInProgress;
    private IReadOnlyList<ProfileStatus> _profiles = [];
    private string? _warning;

    public DictumDaemon(
        DictumConfig config,
        Func<DictumConfig, IDictationEngine> createEngine,
        PlatformServices platform,
        ISoundPlayer sounds,
        ILog log)
    {
        _createEngine = createEngine;
        _platform = platform;
        _sounds = sounds;
        _log = log;
        _engine = createEngine(config);
    }

    public DictumConfig Config => _engine.Config;

    public PipelineState State => _engine.Pipeline.State;

    /// <summary>Raised after every state change, for tests and status displays.</summary>
    public event Action<PipelineState>? StateChanged;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_platform.Unsupported is { } reason)
        {
            _warning = reason;
            _log.Warn(reason);
        }
        else if (_platform.CreateHotkeys is { } createHotkeys)
        {
            _hotkeys = createHotkeys();
            _hotkeys.Triggered += OnHotkey;
        }

        Subscribe(_engine);
        await RegisterHotkeysAsync(ct).ConfigureAwait(false);
        _ = WarmUpAsync(_engine);
        _ = PrepareAsync();
    }

    /// <summary>Applies a reloaded config: new providers, re-registered hotkeys.</summary>
    public async Task ApplyConfigAsync(DictumConfig config, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IDictationEngine previous;
        try
        {
            previous = _engine;
            _engine = _createEngine(config);
            Subscribe(_engine);
        }
        finally
        {
            _gate.Release();
        }

        await previous.DisposeAsync().ConfigureAwait(false);
        await RegisterHotkeysAsync(ct).ConfigureAwait(false);
        _ = WarmUpAsync(_engine);
        _log.Info("Config reloaded");
    }

    public async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Command)
            {
                case "status":
                    break;
                case "start":
                    await StartDictationAsync(ProfileFor(request.Profile), ct).ConfigureAwait(false);
                    break;
                case "stop":
                    _ = StopDictationAsync(_activeProfile);
                    break;
                case "toggle":
                    if (State == PipelineState.Recording)
                    {
                        _ = StopDictationAsync(_activeProfile);
                    }
                    else
                    {
                        await StartDictationAsync(ProfileFor(request.Profile), ct).ConfigureAwait(false);
                    }

                    break;
                case "cancel":
                    await CancelAsync().ConfigureAwait(false);
                    break;
                default:
                    return IpcResponse.Failure($"Unknown command '{request.Command}'.");
            }

            return IpcResponse.Success(Status());
        }
        catch (Exception ex) when (ex is ConfigException or InvalidOperationException)
        {
            return IpcResponse.Failure(ex.Message);
        }
    }

    public DaemonStatus Status() => new(
        State.ToString(),
        _activeProfile,
        _profiles,
        _hotkeys?.Capabilities.Backend ?? "none (use 'dictum toggle')",
        _platform.Injector.Capabilities.Backend,
        Environment.ProcessId,
        _warning);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_hotkeys is not null)
        {
            _hotkeys.Triggered -= OnHotkey;
            await _hotkeys.DisposeAsync().ConfigureAwait(false);
        }

        await CancelAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    private void OnHotkey(HotkeyEvent hotkeyEvent)
    {
        var profile = Config.Profiles.FirstOrDefault(p => p.Id == hotkeyEvent.BindingId);
        if (profile is null)
        {
            return;
        }

        var recordingThis = State == PipelineState.Recording && _activeProfile == profile.Id;
        var action = (hotkeyEvent.Phase, profile.Mode) switch
        {
            (HotkeyPhase.Pressed, HotkeyMode.Toggle) when recordingThis => StopDictationAsync(profile.Id),
            (HotkeyPhase.Pressed, _) when State == PipelineState.Idle => StartDictationAsync(profile.Id, _stopping.Token),
            (HotkeyPhase.Released, HotkeyMode.PushToTalk) when recordingThis => StopDictationAsync(profile.Id),
            _ => Task.CompletedTask,
        };

        _ = Observe(action);
    }

    private async Task StartDictationAsync(string profileId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != PipelineState.Idle)
            {
                throw new InvalidOperationException($"Already busy ({State}).");
            }

            _activeProfile = profileId;
            _dictation?.Dispose();
            _dictation = new CancellationTokenSource();
            await _engine.Pipeline.StartRecordingAsync(Config.General.AudioDevice, ct).ConfigureAwait(false);
            Cue(SoundCue.Start);
        }
        catch
        {
            _activeProfile = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopDictationAsync(string? profileId)
    {
        DictationProfile profile;
        CancellationToken token;
        IDictationEngine engine;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != PipelineState.Recording || profileId is null || _stopInProgress)
            {
                return;
            }

            _stopInProgress = true;

            engine = _engine;
            profile = engine.ResolveProfile(profileId);
            token = _dictation?.Token ?? CancellationToken.None;
        }
        finally
        {
            _gate.Release();
        }

        Cue(SoundCue.Stop);
        var output = InjectorOutput.For(_platform.Injector, engine.Config.Profiles.First(p => p.Id == profileId), engine.Config.General);
        try
        {
            var result = await engine.Pipeline.StopAsync(profile, output, token).ConfigureAwait(false);
            _log.Info($"Dictation done: {result.AudioDuration.TotalSeconds:F1} s audio, {_log.Redact(result.Text)}");
        }
        catch (OperationCanceledException)
        {
            _log.Info("Dictation cancelled");
        }
        catch (Exception ex)
        {
            await ReportAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            _activeProfile = null;
            _stopInProgress = false;
        }
    }

    private async Task CancelAsync()
    {
        if (_dictation is { } dictation)
        {
            await dictation.CancelAsync().ConfigureAwait(false);
        }

        await _engine.Pipeline.CancelAsync().ConfigureAwait(false);
        _activeProfile = null;
    }

    private async Task RegisterHotkeysAsync(CancellationToken ct)
    {
        var bindings = new List<HotkeyBinding>();
        foreach (var profile in Config.Profiles)
        {
            if (profile.Hotkey is { } text && Hotkey.TryParse(text, out var hotkey, out _))
            {
                bindings.Add(new HotkeyBinding(profile.Id, hotkey, $"Dictum: {profile.Name ?? profile.Id}"));
            }
        }

        var registered = false;
        if (_hotkeys is not null)
        {
            try
            {
                await _hotkeys.SetBindingsAsync(bindings, ct).ConfigureAwait(false);
                registered = true;
                _warning = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var hint = (ex as HotkeyUnavailableException)?.Hint;
                _warning = hint is null ? ex.Message : $"{ex.Message} {hint}";
                if (ex is not HotkeyUnavailableException)
                {
                    _log.Error($"Unexpected hotkey backend failure: {ex.Message}", ex);
                }

                _log.Warn($"Hotkeys unavailable: {_warning}");
                await _platform.Notifier.NotifyAsync("Dictum: hotkeys unavailable", _warning, NotificationKind.Error, ct).ConfigureAwait(false);
            }
        }

        _profiles = [.. Config.Profiles.Select(p => new ProfileStatus(
            p.Id,
            p.Hotkey is { } h && Hotkey.TryParse(h, out var parsed, out _) ? parsed.ToString() : null,
            p.Mode.ToString(),
            registered && bindings.Any(b => b.Id == p.Id)))];
        foreach (var binding in bindings)
        {
            _log.Info($"Hotkey {binding.Hotkey} → profile '{binding.Id}'{(registered ? "" : " (not registered)")}");
        }
    }

    private void Subscribe(IDictationEngine engine) =>
        engine.Pipeline.StateChanged += state => StateChanged?.Invoke(state);

    private string ProfileFor(string? requested) =>
        requested ?? _activeProfile ?? Config.Profiles.FirstOrDefault()?.Id
            ?? throw new ConfigException("No profiles in the config.");

    private async Task WarmUpAsync(IDictationEngine engine)
    {
        try
        {
            await engine.WarmUpAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Warm-up failed: {ex.Message}");
        }
    }

    /// <summary>Platform setup that may show a permission dialog; the daemon keeps serving meanwhile.</summary>
    private async Task PrepareAsync()
    {
        if (_platform.PrepareAsync is not { } prepare)
        {
            return;
        }

        try
        {
            await prepare(_stopping.Token).ConfigureAwait(false);
        }
        catch (InjectionUnavailableException ex)
        {
            _log.Warn($"Paste setup: {ex.Message}");
            await _platform.Notifier.NotifyAsync("Dictum: paste unavailable", ex.Message, NotificationKind.Error, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Platform setup failed: {ex.Message}");
        }
    }

    private void Cue(SoundCue cue)
    {
        if (Config.General.Sounds)
        {
            _ = _sounds.PlayAsync(cue, _stopping.Token);
        }
    }

    private async Task ReportAsync(Exception ex)
    {
        var message = ex is DictationException { InnerException: { } inner } ? inner.Message : ex.Message;
        _log.Error($"Dictation failed: {message}", ex);
        Cue(SoundCue.Error);
        await _platform.Notifier.NotifyAsync("Dictum: dictation failed", message, NotificationKind.Error, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReportAsync(ex).ConfigureAwait(false);
        }
    }
}
