using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Dictum.App.Resources;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Daemon;

/// <summary>The supervisor's timings and restart budget; tests shorten them.</summary>
public sealed record SupervisorOptions
{
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    public int MaxRestarts { get; init; } = 3;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ConfigPollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Keeps the desktop app connected to <c>dictum daemon</c>: uses a running daemon or starts one, subscribes to its
/// events, and starts it again when it goes away. Events are raised on a background thread.
/// </summary>
public sealed class DaemonSupervisor : IDaemonChannel, IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly Func<bool> _configExists;
    private readonly IDaemonLauncher _launcher;
    private readonly ILog _log;
    private readonly string _logFile;
    private readonly TimeProvider _time;
    private readonly SupervisorOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _restarts = new();
    private TaskCompletionSource _restartRequested = Signal();
    private TaskCompletionSource _configSaved = Signal();
    private TaskCompletionSource _stopping = Signal();
    private volatile DaemonLink _link = new(DaemonLinkState.Starting);
    private IDaemonProcess? _process;
    private Task _loop = Task.CompletedTask;
    private string? _lastProblem;
    private bool _freeStart = true;
    private volatile bool _quitting;
    private volatile bool _announcedStop;

    /// <param name="logFile">The GUI log, named in problem texts as the place to look.</param>
    public DaemonSupervisor(
        string socketPath,
        Func<bool> configExists,
        IDaemonLauncher launcher,
        ILog log,
        string logFile,
        TimeProvider? time = null,
        SupervisorOptions? options = null)
    {
        _socketPath = socketPath;
        _configExists = configExists;
        _launcher = launcher;
        _log = log;
        _logFile = logFile;
        _time = time ?? TimeProvider.System;
        _options = options ?? new SupervisorOptions();
    }

    public event Action<DaemonLink>? LinkChanged;

    public event Action<IpcEvent>? EventReceived;

    public DaemonLink Link => _link;

    public void Start() => _loop = Task.Run(() => RunAsync(_stop.Token));

    public Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null) =>
        new IpcClient(_socketPath).SendAsync(request, ct, timeout);

    /// <summary>"Restart daemon": tries again after <see cref="DaemonLinkState.Failed"/>, or replaces a daemon from another version.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _restartRequested.TrySetResult();
        }
    }

    /// <summary>A config file was just written (the wizard's save): stop waiting for the next poll.</summary>
    public void ConfigSaved()
    {
        lock (_gate)
        {
            _configSaved.TrySetResult();
        }
    }

    /// <summary>
    /// Quit Dictum: asks the daemon to stop, waits up to <see cref="SupervisorOptions.QuitTimeout"/> for its
    /// <c>stopping</c> event, and stops supervising. A daemon that does not stop in time is left running.
    /// </summary>
    public async Task QuitAsync()
    {
        _quitting = true;
        var connected = _link.State == DaemonLinkState.Connected;
        if (connected)
        {
            using var timeout = new CancellationTokenSource(_options.QuitTimeout);
            try
            {
                await SendAsync(new IpcRequest("shutdown"), timeout.Token, _options.QuitTimeout).ConfigureAwait(false);
                await _stopping.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DaemonNotRunningException or IOException or SocketException or OperationCanceledException)
            {
                _log.Warn($"The daemon did not confirm that it stopped: {ex.Message}");
            }
        }

        await StopLoopAsync().ConfigureAwait(false);
        if (!connected && _process is { } process && !process.Exited.IsCompleted)
        {
            // A daemon we started that has not answered yet holds nothing worth saving, and Quit stops both.
            try
            {
                await _launcher.EndAsync(process.Id, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Quit goes on whatever happens to the starting daemon.
                _log.Warn($"Could not end the daemon that was starting: {ex.Message}");
            }
        }

        SetLink(new DaemonLink(DaemonLinkState.Stopped));
    }

    /// <summary>Stops supervising; the daemon keeps running.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopLoopAsync().ConfigureAwait(false);
        _process?.Dispose();
        _stop.Dispose();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task StopLoopAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        await _loop.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A bug or an unforeseen failure must not end supervision: "Restart daemon" needs a loop that waits for it.
                _log.Error($"Daemon supervision failed: {ex.Message}", ex);
                try
                {
                    await FailAsync(Format(Strings.ProblemUnexpected, _logFile), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (_quitting)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        // A fresh signal before the check: a save between the check and the wait still counts, and a nudge from an
        // earlier wait does not turn this one into a busy loop.
        Task saved;
        lock (_gate)
        {
            _configSaved = Signal();
            saved = _configSaved.Task;
        }

        if (!_configExists())
        {
            SetLink(new DaemonLink(DaemonLinkState.NotConfigured));
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(saved, Task.Delay(_options.ConfigPollInterval, poll.Token)).ConfigureAwait(false);
            await poll.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return;
        }

        var subscription = await TryConnectAsync(ct).ConfigureAwait(false);
        if (subscription is null)
        {
            if (!TakeStart())
            {
                await FailAsync(_lastProblem ?? Format(Strings.ProblemKeepsStopping, _logFile), ct).ConfigureAwait(false);
                return;
            }

            var started = await StartDaemonAsync(ct).ConfigureAwait(false);
            if (started.Subscription is null)
            {
                if (started.Problem is null)
                {
                    return;
                }

                _lastProblem = started.Problem;
                _log.Warn(started.Problem!);
                if (started.Fatal)
                {
                    await FailAsync(started.Problem!, ct).ConfigureAwait(false);
                }

                return;
            }

            subscription = started.Subscription;
        }

        await using (subscription.ConfigureAwait(false))
        {
            var response = subscription.Response;
            if (!response.Ok || response.Status is not { Protocol: IpcProtocol.Version } status)
            {
                var pid = response.Status?.ProcessId ?? await TryGetProcessIdAsync(ct).ConfigureAwait(false);
                var restart = NewRestartSignal();
                SetLink(new DaemonLink(DaemonLinkState.VersionMismatch, response.Status, Strings.ProblemVersionMismatch));
                await AwaitRestartAsync(restart, ct).ConfigureAwait(false);
                await ReplaceAsync(pid, ct).ConfigureAwait(false);
                return;
            }

            _lastProblem = null;
            _stopping = Signal();
            _announcedStop = false;
            SetLink(new DaemonLink(DaemonLinkState.Connected, status));
            await PumpAsync(subscription, ct).ConfigureAwait(false);
        }

        if (_quitting || ct.IsCancellationRequested)
        {
            return;
        }

        if (_announcedStop)
        {
            // It said it was stopping: someone stopped it on purpose ('dictum shutdown', Ctrl+C, systemctl stop). Only a
            // daemon that exits unexpectedly is started again, so the tray offers "Restart daemon" instead.
            _log.Info("The daemon was stopped; not starting it again");
            await FailAsync(Strings.ProblemStoppedOnPurpose, ct).ConfigureAwait(false);
            return;
        }

        _log.Warn("Lost the connection to the daemon");
        SetLink(new DaemonLink(DaemonLinkState.Restarting));
    }

    private sealed record StartResult(IpcSubscription? Subscription, string? Problem, bool Fatal = false);

    private async Task<StartResult> StartDaemonAsync(CancellationToken ct)
    {
        if (_link.State != DaemonLinkState.Restarting)
        {
            SetLink(new DaemonLink(DaemonLinkState.Starting));
        }

        if (_quitting)
        {
            return new StartResult(null, null);
        }

        IDaemonProcess process;
        try
        {
            process = _launcher.Launch();
        }
        catch (DaemonLaunchException ex)
        {
            return new StartResult(null, ex.Message, Fatal: true);
        }

        _process?.Dispose();
        _process = process;
        _log.Info($"Started the daemon (pid {process.Id})");
        var waited = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
            if (await TryConnectAsync(ct).ConfigureAwait(false) is { } subscription)
            {
                return new StartResult(subscription, null);
            }

            if (process.Exited.IsCompleted)
            {
                return new StartResult(null, Format(Strings.ProblemExitedWhileStarting, await process.Exited.ConfigureAwait(false), _logFile));
            }

            if (waited.Elapsed >= _options.StartTimeout)
            {
                return new StartResult(null, Format(Strings.ProblemStartTimeout, _options.StartTimeout.TotalSeconds, _logFile));
            }
        }
    }

    /// <summary>The first start after the app starts, or after "Restart daemon", is free; later ones count.</summary>
    private bool TakeStart()
    {
        lock (_gate)
        {
            if (_freeStart)
            {
                _freeStart = false;
                return true;
            }

            var now = _time.GetUtcNow();
            while (_restarts.TryPeek(out var at) && now - at >= _options.RestartWindow)
            {
                _restarts.Dequeue();
            }

            if (_restarts.Count >= _options.MaxRestarts)
            {
                return false;
            }

            _restarts.Enqueue(now);
            return true;
        }
    }

    private async Task FailAsync(string problem, CancellationToken ct)
    {
        var restart = NewRestartSignal();
        SetLink(new DaemonLink(DaemonLinkState.Failed, Problem: problem));
        await AwaitRestartAsync(restart, ct).ConfigureAwait(false);
    }

    /// <summary>A fresh signal, made before the link shows the problem, so a click from before does not count.</summary>
    private Task NewRestartSignal()
    {
        lock (_gate)
        {
            _restartRequested = Signal();
            return _restartRequested.Task;
        }
    }

    private async Task AwaitRestartAsync(Task restart, CancellationToken ct)
    {
        await restart.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _restarts.Clear();
            _freeStart = true;
            _lastProblem = null;
        }
    }

    private async Task<IpcSubscription?> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            return await new IpcClient(_socketPath).SubscribeAsync(ct).ConfigureAwait(false);
        }
        catch (DaemonNotRunningException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A daemon on its way out, or one that does not answer within the client's timeout.
            _log.Debug($"Could not subscribe: {ex.Message}");
            return null;
        }
    }

    private async Task<int?> TryGetProcessIdAsync(CancellationToken ct)
    {
        try
        {
            return (await SendAsync(new IpcRequest("status"), ct).ConfigureAwait(false)).Status?.ProcessId;
        }
        catch (Exception ex) when (ex is DaemonNotRunningException or IOException or SocketException or JsonException or InvalidOperationException
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // An old daemon that is slow or odd to answer: it is replaced by asking it to stop, without its pid.
            _log.Debug($"No pid from the other daemon: {ex.Message}");
            return null;
        }
    }

    /// <summary>Asks a daemon from another version to stop, ends it if it does not, and lets the loop start ours.</summary>
    private async Task ReplaceAsync(int? processId, CancellationToken ct)
    {
        try
        {
            var answer = await SendAsync(new IpcRequest("shutdown"), ct, _options.QuitTimeout).ConfigureAwait(false);
            if (!answer.Ok)
            {
                _log.Info($"The other daemon refused 'shutdown': {answer.Error}");
            }
        }
        catch (Exception ex) when (ex is DaemonNotRunningException or IOException or SocketException
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.Debug($"Could not send 'shutdown': {ex.Message}");
        }

        if (processId is { } pid)
        {
            await _launcher.EndAsync(pid, _options.QuitTimeout, ct).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(IpcSubscription subscription, CancellationToken ct)
    {
        try
        {
            await foreach (var message in subscription.ReadEventsAsync(ct).ConfigureAwait(false))
            {
                if (message is { Event: "status", Status: { } status })
                {
                    SetLink(new DaemonLink(DaemonLinkState.Connected, status));
                }
                else if (message.Event == "stopping")
                {
                    _announcedStop = true;
                    _stopping.TrySetResult();
                }

                Raise(() => EventReceived?.Invoke(message));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _log.Debug($"The event stream ended: {ex.Message}");
        }
        finally
        {
            _stopping.TrySetResult();
        }
    }

    private void SetLink(DaemonLink link)
    {
        _link = link;
        Raise(() => LinkChanged?.Invoke(link));
    }

    /// <summary>A handler that throws must not end supervision.</summary>
    private void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _log.Error($"A daemon event handler failed: {ex.Message}", ex);
        }
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
