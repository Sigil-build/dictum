using System.Diagnostics;
using Dictum.Audio;
using Dictum.Audio.PortAudio;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Hotkeys;
using Dictum.Core.Pipeline;
using Dictum.Core.Stt;
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
    /// <summary>Must match the LogicalName of the sample in Dictum.Host.csproj.</summary>
    private const string SampleResource = "Dictum.Host.Samples.uk.wav";

    private const int MinTestSeconds = 1;
    private const int MaxTestSeconds = 30;

    /// <summary>The expiry check runs a few times per monitor lifetime, within these bounds.</summary>
    private const int MonitorChecksPerLifetime = 4;
    private const double MinMonitorCheckMs = 20;
    private const double MaxMonitorCheckMs = 1000;

    private readonly Func<DictumConfig, IDictationEngine> _createEngine;
    private readonly PlatformServices _platform;
    private readonly ISoundPlayer _sounds;
    private readonly ILog _log;
    private readonly IpcEventHub _events;
    private readonly TimeSpan _monitorLifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDictationEngine _engine;
    private IHotkeyService? _hotkeys;
    private IReadOnlyList<ProfileStatus> _profiles = [];
    private IReadOnlyList<ConfigIssue> _configWarnings;
    private (DictumConfig Config, IReadOnlyList<ConfigIssue>? Warnings)? _pendingConfig;
    private Action<PipelineState>? _stateHandler;
    private Action<AudioFrame>? _frameHandler;
    private CancellationTokenSource? _dictation;
    private string? _activeProfile;
    private string? _selectedProfile;
    private string? _warning;
    private volatile bool _warmedUp;
    private bool _stopInProgress;
    private bool _paused;
    private bool _monitoring;
    private DateTimeOffset _monitorUntil;
    private bool _testing;
    private bool _stoppingAnnounced;

    public DictumDaemon(
        DictumConfig config,
        Func<DictumConfig, IDictationEngine> createEngine,
        PlatformServices platform,
        ISoundPlayer sounds,
        ILog log,
        IpcEventHub? events = null,
        IReadOnlyList<ConfigIssue>? configWarnings = null,
        TimeSpan? monitorLifetime = null)
    {
        _createEngine = createEngine;
        _platform = platform;
        _sounds = sounds;
        _log = log;
        _events = events ?? new IpcEventHub();
        _configWarnings = configWarnings ?? [];
        _monitorLifetime = monitorLifetime ?? IpcProtocol.MonitorLifetime;
        _engine = createEngine(config);
    }

    public DictumConfig Config => _engine.Config;

    /// <summary>Completes when a client sent <c>shutdown</c>; the host then disposes the daemon and exits.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <summary>Re-reads the config for the <c>reload</c> command; set by the host that owns the config file.</summary>
    public Func<CancellationToken, Task>? ReloadHandler { get; set; }

    /// <summary>Runs the doctor checks for the <c>doctor</c> command; set by the host, which knows the paths.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<PlatformCheck>>>? DoctorHandler { get; set; }

    public IDictationEngine Engine => _engine;

    public IReadOnlyList<ConfigIssue> ConfigWarnings => _configWarnings;

    public PipelineState State => _engine.Pipeline.State;

    public event Action<PipelineState>? StateChanged;

    /// <summary>
    /// Tells subscribers that the daemon stops, and waits until they have been sent it. Call before the IPC server is
    /// disposed: a client that sees the socket close without this event takes it for a crash and restarts the daemon.
    /// </summary>
    public async Task AnnounceStoppingAsync(TimeSpan timeout)
    {
        if (TryAnnounceStopping())
        {
            await _events.WhenDeliveredAsync(timeout).ConfigureAwait(false);
        }
    }

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
    public async Task ApplyConfigAsync(DictumConfig config, CancellationToken ct, IReadOnlyList<ConfigIssue>? warnings = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IDictationEngine previous;
        try
        {
            if (State != PipelineState.Idle || _stopInProgress || _testing)
            {
                // Swapping the engine now would drop the dictation; the newest config is applied once it has finished.
                _pendingConfig = (config, warnings);
                _log.Info("Config reload waits for the dictation to finish");
                return;
            }

            _pendingConfig = null;
            await StopMonitorCoreAsync().ConfigureAwait(false);
            previous = _engine;
            Unsubscribe(previous);
            _engine = _createEngine(config);
            _warmedUp = false;
            Subscribe(_engine);
        }
        finally
        {
            _gate.Release();
        }

        _configWarnings = warnings ?? [];
        if (_selectedProfile is { } selected && config.Profiles.All(p => p.Id != selected))
        {
            _selectedProfile = null;
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
            return await DispatchAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConfigException or InvalidOperationException or AudioDeviceException)
        {
            return IpcResponse.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A provider that is down or a server that times out must not look like a dead daemon to the client.
            _log.Error($"IPC '{request.Command}' failed: {ex.Message}", ex);
            return IpcResponse.Failure(MessageOf(ex));
        }
    }

    public DaemonStatus Status() => new(
        State.ToString(),
        _activeProfile,
        _profiles,
        _hotkeys?.Capabilities.Backend ?? "none (use 'dictum toggle')",
        _platform.Injector.Capabilities.Backend,
        Environment.ProcessId,
        _warning,
        Paused: _paused,
        SelectedProfile: _selectedProfile,
        ConfigWarnings: _configWarnings.Count == 0 ? null : [.. _configWarnings.Select(w => w.ToString())],
        WarmedUp: _warmedUp);

    public async ValueTask DisposeAsync()
    {
        TryAnnounceStopping();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_hotkeys is not null)
        {
            _hotkeys.Triggered -= OnHotkey;
            await _hotkeys.DisposeAsync().ConfigureAwait(false);
        }

        await CancelAsync().ConfigureAwait(false);
        await StopMonitorCoreAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            case IpcCommand.Status:
            case IpcCommand.Subscribe:
                break;
            case IpcCommand.Start:
                await StartDictationAsync(ProfileFor(request.Profile), ct).ConfigureAwait(false);
                break;
            case IpcCommand.Stop:
                _ = StopDictationAsync(_activeProfile);
                break;
            case IpcCommand.Toggle:
                await ToggleAsync(request.Profile, ct).ConfigureAwait(false);
                break;
            case IpcCommand.Cancel:
                await CancelAsync().ConfigureAwait(false);
                break;
            case IpcCommand.Reload:
                if (ReloadHandler is null)
                {
                    return IpcResponse.Failure("This daemon cannot reload its config.");
                }

                await ReloadHandler(ct).ConfigureAwait(false);
                break;
            case IpcCommand.Shutdown:
                await CancelAsync().ConfigureAwait(false);
                _shutdown.TrySetResult();
                break;
            case IpcCommand.PauseHotkeys:
            case IpcCommand.ResumeHotkeys:
                _paused = request.Command == IpcCommand.PauseHotkeys;
                await RegisterHotkeysAsync(ct).ConfigureAwait(false);
                break;
            case IpcCommand.UseProfile:
                SelectProfile(request.Profile);
                break;
            case IpcCommand.Devices:
                return new IpcResponse(true, Status: Status(), Devices: _engine.Audio.ListDevices());
            case IpcCommand.Doctor:
                if (DoctorHandler is null)
                {
                    return IpcResponse.Failure("This daemon cannot run the checks.");
                }

                return new IpcResponse(true, Status: Status(), Checks: await DoctorHandler(ct).ConfigureAwait(false));
            case IpcCommand.TestStt:
                return new IpcResponse(true, Status: Status(), Test: await TestSttAsync(request, ct).ConfigureAwait(false));
            case IpcCommand.MonitorStart:
                await StartMonitorAsync(request.Device, ct).ConfigureAwait(false);
                break;
            case IpcCommand.MonitorStop:
                await StopMonitorAsync(ct).ConfigureAwait(false);
                break;
            default:
                return IpcResponse.Failure($"Unknown command '{request.Command}'.");
        }

        return IpcResponse.Success(Status());
    }

    private bool TryAnnounceStopping()
    {
        if (_stoppingAnnounced)
        {
            return false;
        }

        _stoppingAnnounced = true;
        _events.Publish(IpcEvent.Stopping(_shutdown.Task.IsCompleted ? StoppingReason.Shutdown : StoppingReason.Exit));
        return true;
    }

    private async Task ToggleAsync(string? profile, CancellationToken ct)
    {
        if (State == PipelineState.Recording)
        {
            _ = StopDictationAsync(_activeProfile);
        }
        else
        {
            await StartDictationAsync(ProfileFor(profile), ct).ConfigureAwait(false);
        }
    }

    private void SelectProfile(string? profile)
    {
        _selectedProfile = profile is { } id && Config.Profiles.Any(p => p.Id == id)
            ? id
            : throw new ConfigException($"Unknown profile '{profile}'.");
        _events.Publish(IpcEvent.ForStatus(Status()));
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

            if (_testing)
            {
                throw new InvalidOperationException("A speech provider test is recording; try again in a few seconds.");
            }

            await StopMonitorCoreAsync().ConfigureAwait(false);

            _activeProfile = profileId;
            _dictation?.Dispose();
            _dictation = new CancellationTokenSource();
            try
            {
                await _engine.Pipeline.StartRecordingAsync(Config.General.AudioDevice, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The pipeline published Recording and then Idle; subscribers also need this dictation to end.
                Finish(DictationOutcome.Error, MessageOf(ex), null);
                throw;
            }

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

            engine = _engine;
            profile = engine.ResolveProfile(profileId);
            token = _dictation?.Token ?? CancellationToken.None;
            _stopInProgress = true;
        }
        finally
        {
            _gate.Release();
        }

        Cue(SoundCue.Stop);
        var config = engine.Config.Profiles.First(p => p.Id == profileId);
        var output = InjectorOutput.For(_platform.Injector, config, engine.Config.General);
        try
        {
            var result = await engine.Pipeline.StopAsync(profile, output, token).ConfigureAwait(false);
            _log.Info($"Dictation done: {result.AudioDuration.TotalSeconds:F1} s audio, {_log.Redact(result.Text)}");
            Finish(
                result.Text.Length == 0 ? DictationOutcome.Empty
                    : config.Output == OutputMode.Clipboard ? DictationOutcome.Copied
                    : DictationOutcome.Pasted,
                null,
                result);
        }
        catch (OperationCanceledException)
        {
            _log.Info("Dictation cancelled");
            Finish(DictationOutcome.Cancelled, null, null);
        }
        catch (Exception ex)
        {
            await ReportAsync(ex).ConfigureAwait(false);
            Finish(DictationOutcome.Error, MessageOf(ex), null);
        }
        finally
        {
            _activeProfile = null;
            _stopInProgress = false;
        }

        await ApplyPendingConfigAsync().ConfigureAwait(false);
    }

    private async Task CancelAsync()
    {
        // Claiming the dictation under the gate makes a stop that races the cancel return early, so one dictation never
        // finishes twice.
        bool wasRecording;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            wasRecording = State == PipelineState.Recording && !_stopInProgress;
            if (wasRecording)
            {
                _stopInProgress = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (_dictation is { } dictation)
        {
            await dictation.CancelAsync().ConfigureAwait(false);
        }

        await _engine.Pipeline.CancelAsync().ConfigureAwait(false);
        _activeProfile = null;
        if (wasRecording)
        {
            _stopInProgress = false;
            Finish(DictationOutcome.Cancelled, null, null);
        }

        await ApplyPendingConfigAsync().ConfigureAwait(false);
    }

    /// <summary>Applies a config that arrived during a dictation or a provider test, now that it has ended.</summary>
    private async Task ApplyPendingConfigAsync()
    {
        if (_pendingConfig is not { } pending || _stopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ApplyConfigAsync(pending.Config, _stopping.Token, pending.Warnings).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Config reload failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task<SttTestResult> TestSttAsync(IpcRequest request, CancellationToken ct)
    {
        var id = request.Stt ?? throw new ConfigException("Name the speech provider to test.");
        var sample = request.Sample == true;
        IDictationEngine engine;
        ISpeechToText stt;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_testing || (!sample && (State != PipelineState.Idle || _monitoring)))
            {
                throw new InvalidOperationException("Dictum is busy; test the provider when the dictation or level meter has finished.");
            }

            engine = _engine;
            stt = engine.GetStt(id);
            if (!sample)
            {
                await engine.Audio.StartAsync(Config.General.AudioDevice, ct).ConfigureAwait(false);
            }

            // Holds off hotkey dictations and config reloads until the test is over, so its engine stays alive.
            _testing = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            AudioClip clip;
            if (sample)
            {
                clip = LoadSample();
            }
            else
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(request.Seconds ?? IpcProtocol.DefaultTestSeconds, MinTestSeconds, MaxTestSeconds)), ct).ConfigureAwait(false);
                }
                finally
                {
                    clip = await engine.Audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            var language = engine.Config.Profiles.FirstOrDefault(p => p.Stt == id)?.Language;
            var watch = Stopwatch.StartNew();
            var transcript = await stt.TranscribeAsync(clip, SttRequest.ForSetting(language, null) ?? new SttRequest(null, null), ct).ConfigureAwait(false);
            return new SttTestResult(transcript.Text.Trim(), transcript.Language, watch.ElapsedMilliseconds, transcript.Backend, transcript.Model,
                clip.Duration.TotalSeconds);
        }
        finally
        {
            _testing = false;
            await ApplyPendingConfigAsync().ConfigureAwait(false);
        }
    }

    /// <summary>6.9 s of Ukrainian speech ("тестовий запис"), synthesised with Piper TTS from a CC0 dataset.</summary>
    private static AudioClip LoadSample()
    {
        using var stream = typeof(DictumDaemon).Assembly.GetManifestResourceStream(SampleResource)
            ?? throw new InvalidOperationException("The built-in speech sample is missing from this build.");
        return Wav.ReadClip(stream);
    }

    private async Task StartMonitorAsync(string? device, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != PipelineState.Idle)
            {
                throw new InvalidOperationException("A dictation is running; the level meter can start when it has finished.");
            }

            _monitorUntil = DateTimeOffset.UtcNow + _monitorLifetime;
            if (_monitoring)
            {
                return;
            }

            await _engine.Audio.StartAsync(device ?? Config.General.AudioDevice, ct, keepSamples: false).ConfigureAwait(false);
            _monitoring = true;
        }
        finally
        {
            _gate.Release();
        }

        _ = Observe(ExpireMonitorAsync());
    }

    /// <summary>Closes a monitor its client stopped renewing, e.g. after the desktop app crashed.</summary>
    private async Task ExpireMonitorAsync()
    {
        var step = TimeSpan.FromMilliseconds(Math.Clamp(
            _monitorLifetime.TotalMilliseconds / MonitorChecksPerLifetime, MinMonitorCheckMs, MaxMonitorCheckMs));
        while (_monitoring)
        {
            await Task.Delay(step, _stopping.Token).ConfigureAwait(false);
            if (_monitoring && DateTimeOffset.UtcNow >= _monitorUntil)
            {
                await _gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                try
                {
                    if (DateTimeOffset.UtcNow >= _monitorUntil)
                    {
                        await StopMonitorCoreAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
    }

    private async Task StopMonitorAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopMonitorCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Call with the gate held.</summary>
    private async Task StopMonitorCoreAsync()
    {
        if (!_monitoring)
        {
            return;
        }

        _monitoring = false;
        await _engine.Audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RegisterHotkeysAsync(CancellationToken ct)
    {
        var bindings = new List<HotkeyBinding>();
        foreach (var profile in _paused ? [] : Config.Profiles)
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

        _events.Publish(IpcEvent.ForStatus(Status()));
    }

    private void Subscribe(IDictationEngine engine)
    {
        _stateHandler = state =>
        {
            StateChanged?.Invoke(state);
            _events.Publish(IpcEvent.ForState(state, _activeProfile, ModeOf(_activeProfile)));
        };
        _frameHandler = frame =>
        {
            if (State == PipelineState.Recording || _monitoring || _testing)
            {
                _events.Publish(IpcEvent.ForFrame(frame));
            }
        };
        engine.Pipeline.StateChanged += _stateHandler;
        engine.Audio.FrameCaptured += _frameHandler;
    }

    private void Unsubscribe(IDictationEngine engine)
    {
        engine.Pipeline.StateChanged -= _stateHandler;
        engine.Audio.FrameCaptured -= _frameHandler;
    }

    private string? ModeOf(string? profileId) => Config.Profiles.FirstOrDefault(p => p.Id == profileId)?.Mode.ToString();

    private void Finish(string outcome, string? message, DictationResult? result) =>
        _events.Publish(IpcEvent.Finished(
            outcome,
            message,
            result?.Language,
            (long)(result?.AudioDuration.TotalMilliseconds ?? 0),
            (long?)result?.Stages.FirstOrDefault(s => s.Stage == StageTiming.Transcribe)?.Elapsed.TotalMilliseconds));

    private static string MessageOf(Exception ex) =>
        ex is DictationException { InnerException: { } inner } ? inner.Message : ex.Message;

    private string ProfileFor(string? requested) =>
        requested is null
            ? _selectedProfile ?? Config.Profiles.FirstOrDefault()?.Id ?? throw new ConfigException("No profiles in the config.")
            : Config.Profiles.Any(p => p.Id == requested) ? requested : throw new ConfigException($"Unknown profile '{requested}'.");

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

        // A failed warm-up ends "loading" too: the first dictation reports the problem. An engine a reload replaced
        // meanwhile says nothing.
        if (ReferenceEquals(engine, _engine) && !_stopping.IsCancellationRequested)
        {
            _warmedUp = true;
            _events.Publish(IpcEvent.ForStatus(Status()));
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
        var message = MessageOf(ex);
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
