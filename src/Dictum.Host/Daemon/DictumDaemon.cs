using Dictum.Audio;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
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
/// <remarks>
/// One gate orders the dictation, the provider test, the level meter and the engine swap: each checks the others' state
/// and changes its own while holding it.
/// </remarks>
public sealed class DictumDaemon : IAsyncDisposable
{
    private readonly Func<DictumConfig, IDictationEngine> _createEngine;
    private readonly PlatformServices _platform;
    private readonly ISoundPlayer _sounds;
    private readonly ILog _log;
    private readonly IpcEventHub _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HotkeyRegistrar _hotkeys;
    private readonly LevelMonitor _monitor;
    private readonly SttTester _tester;
    private readonly DaemonCommandRouter _router;
    private readonly BackgroundWork _work = new();
    private IDictationEngine _engine;
    private IReadOnlyList<ConfigIssue> _configWarnings;
    private (DictumConfig Config, IReadOnlyList<ConfigIssue>? Warnings)? _pendingConfig;
    private Action<PipelineState>? _stateHandler;
    private Action<AudioFrame>? _frameHandler;
    private CancellationTokenSource? _dictation;
    private volatile string? _activeProfile;
    private volatile string? _selectedProfile;
    private volatile bool _warmedUp;
    private volatile bool _stopInProgress;
    private bool _stoppingAnnounced;
    private bool _disposed;

    public DictumDaemon(
        DictumConfig config,
        Func<DictumConfig, IDictationEngine> createEngine,
        PlatformServices platform,
        ISoundPlayer sounds,
        ILog log,
        IpcEventHub? events = null,
        IReadOnlyList<ConfigIssue>? configWarnings = null,
        DaemonHostCommands? host = null,
        TimeProvider? time = null)
    {
        _createEngine = createEngine;
        _platform = platform;
        _sounds = sounds;
        _log = log;
        _events = events ?? new IpcEventHub(time);
        _configWarnings = configWarnings ?? [];
        _engine = createEngine(config);
        _hotkeys = new HotkeyRegistrar(platform, log, () => Config, PublishStatus);
        _monitor = new LevelMonitor(_gate, () => _engine, time ?? TimeProvider.System, task => _work.Add(Observe(task)), _stopping.Token);
        _tester = new SttTester(_gate, () => _engine, _monitor, ApplyPendingConfigAsync);
        _router = new DaemonCommandRouter(this, _hotkeys, _tester, _monitor, host, log);
    }

    public DictumConfig Config => _engine.Config;

    /// <summary>Completes when a client sent <c>shutdown</c>; the host then disposes the daemon and exits.</summary>
    public Task ShutdownRequested => _shutdown.Task;

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
        _hotkeys.Start(OnHotkey);
        Subscribe(_engine);
        await _hotkeys.RegisterAsync(ct).ConfigureAwait(false);
        _work.Add(WarmUpAsync(_engine));
        _work.Add(PrepareAsync());
    }

    /// <summary>Applies a reloaded config: new providers, re-registered hotkeys.</summary>
    public Task ApplyConfigAsync(DictumConfig config, CancellationToken ct, IReadOnlyList<ConfigIssue>? warnings = null)
    {
        var applying = ApplyConfigCoreAsync(config, warnings, ct);
        _work.Add(applying);
        return applying;
    }

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        var handling = _router.HandleAsync(request, ct);
        _work.Add(handling);
        return handling;
    }

    public DaemonStatus Status() => new(
        State.ToString(),
        _activeProfile,
        _hotkeys.Profiles,
        _hotkeys.Backend,
        _platform.Injector.Capabilities.Backend,
        Environment.ProcessId,
        _hotkeys.Warning,
        Paused: _hotkeys.Paused,
        SelectedProfile: _selectedProfile,
        ConfigWarnings: _configWarnings.Count == 0 ? null : [.. _configWarnings.Select(w => w.ToString())],
        WarmedUp: _warmedUp);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryAnnounceStopping();
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _hotkeys.CloseAsync().ConfigureAwait(false);
        await CancelAsync().ConfigureAwait(false);

        // What is freed below is in use until the stops, reloads and requests still running have ended.
        await _work.IdleAsync().ConfigureAwait(false);
        await _monitor.StopCoreAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
        _hotkeys.Dispose();
        _dictation?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>The message a client sees: a dictation failure names its cause.</summary>
    internal static string MessageOf(Exception ex) =>
        ex is DictationException { InnerException: { } inner } ? inner.Message : ex.Message;

    /// <summary>The profile a command means: the one it names, else the selected one, else the first.</summary>
    internal string ProfileFor(string? requested) =>
        requested is null
            ? _selectedProfile ?? Config.Profiles.FirstOrDefault()?.Id ?? throw new ConfigException("No profiles in the config.")
            : Config.FindProfile(requested)?.Id ?? throw new ConfigException($"Unknown profile '{requested}'.");

    internal void SelectProfile(string? profile)
    {
        _selectedProfile = Config.FindProfile(profile)?.Id ?? throw new ConfigException($"Unknown profile '{profile}'.");
        PublishStatus();
    }

    internal async Task ToggleAsync(string? profile, CancellationToken ct)
    {
        if (State == PipelineState.Recording)
        {
            StopActiveDictation();
        }
        else
        {
            await StartDictationAsync(ProfileFor(profile), ct).ConfigureAwait(false);
        }
    }

    internal async Task StartDictationAsync(string profileId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != PipelineState.Idle)
            {
                throw new InvalidOperationException($"Already busy ({State}).");
            }

            // The pipeline is idle a moment before the last stop clears its profile, which would also clear a new one's.
            if (_stopInProgress)
            {
                throw new InvalidOperationException("Already busy (finishing the last dictation).");
            }

            if (_tester.IsTesting)
            {
                throw new InvalidOperationException("A speech provider test is recording; try again in a few seconds.");
            }

            await _monitor.StopCoreAsync().ConfigureAwait(false);

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

    /// <summary>Stops the dictation that records, in the background; the client follows it through the events.</summary>
    internal void StopActiveDictation() => _work.Add(StopDictationAsync(_activeProfile));

    internal async Task CancelAsync()
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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A dictation that started after this cancel keeps its profile.
            if (wasRecording || State != PipelineState.Recording)
            {
                _activeProfile = null;
            }

            if (wasRecording)
            {
                _stopInProgress = false;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (wasRecording)
        {
            Finish(DictationOutcome.Cancelled, null, null);
        }

        await ApplyPendingConfigAsync().ConfigureAwait(false);
    }

    internal async Task ShutdownAsync()
    {
        await CancelAsync().ConfigureAwait(false);
        _shutdown.TrySetResult();
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

    private void OnHotkey(HotkeyEvent hotkeyEvent)
    {
        var profile = Config.FindProfile(hotkeyEvent.BindingId);
        if (profile is null)
        {
            return;
        }

        var recordingThis = State == PipelineState.Recording && _activeProfile == profile.Id;
        var action = (hotkeyEvent.Phase, profile.Mode) switch
        {
            (HotkeyPhase.Pressed, HotkeyMode.Toggle) when recordingThis => StopDictationAsync(profile.Id),
            (HotkeyPhase.Pressed, _) when State == PipelineState.Idle && !_stopInProgress => StartDictationAsync(profile.Id, _stopping.Token),
            (HotkeyPhase.Released, HotkeyMode.PushToTalk) when recordingThis => StopDictationAsync(profile.Id),
            _ => Task.CompletedTask,
        };

        _work.Add(Observe(action));
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
        var config = engine.Config.FindProfile(profileId)
            ?? throw new InvalidOperationException($"Profile '{profileId}' resolved but is not in the config.");
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
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _activeProfile = null;
                _stopInProgress = false;
            }
            finally
            {
                _gate.Release();
            }
        }

        await ApplyPendingConfigAsync().ConfigureAwait(false);
    }

    private async Task ApplyConfigCoreAsync(DictumConfig config, IReadOnlyList<ConfigIssue>? warnings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IDictationEngine previous;
        try
        {
            if (State != PipelineState.Idle || _stopInProgress || _tester.IsTesting)
            {
                // Swapping the engine now would drop the dictation; the newest config is applied once it has finished.
                _pendingConfig = (config, warnings);
                _log.Info("Config reload waits for the dictation to finish");
                return;
            }

            _pendingConfig = null;
            await _monitor.StopCoreAsync().ConfigureAwait(false);
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
        await _hotkeys.RegisterAsync(ct).ConfigureAwait(false);
        _work.Add(WarmUpAsync(_engine));
        _log.Info("Config reloaded");
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

    private void Subscribe(IDictationEngine engine)
    {
        _stateHandler = state =>
        {
            StateChanged?.Invoke(state);
            _events.Publish(IpcEvent.ForState(state, _activeProfile, ModeOf(_activeProfile)));
        };
        _frameHandler = frame =>
        {
            if (State == PipelineState.Recording || _monitor.IsMonitoring || _tester.IsTesting)
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

    private void PublishStatus() => _events.Publish(IpcEvent.ForStatus(Status()));

    private string? ModeOf(string? profileId) => Config.FindProfile(profileId)?.Mode.ToString();

    private void Finish(string outcome, string? message, DictationResult? result) =>
        _events.Publish(IpcEvent.Finished(
            outcome,
            message,
            result?.Language,
            (long)(result?.AudioDuration.TotalMilliseconds ?? 0),
            (long?)result?.Stages.FirstOrDefault(s => s.Stage == StageTiming.Transcribe)?.Elapsed.TotalMilliseconds));

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
            PublishStatus();
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
