using System.Globalization;
using Dictum.Core.Diagnostics;

namespace Dictum.Plugins;

public enum PluginInstanceState
{
    /// <summary>No process, and none on the way: the next <see cref="PluginInstance.ReadyAsync"/> checks trust and starts one.</summary>
    Stopped,

    /// <summary>A start, or a restart in its delay, is under way; callers join it.</summary>
    Starting,

    Ready,

    /// <summary>The start failed, or the plugin kept stopping. Callers get the reason until a new lease retries it.</summary>
    Failed,
}

/// <summary>
/// The process of one <see cref="PluginKey"/>: started on the first <see cref="ReadyAsync"/>, shared by every lease,
/// and restarted within the budget when it exits unasked. One lock guards the state, and nothing waits under it.
/// </summary>
/// <remarks>
/// A stop that Dictum asks for detaches the process under the lock before it closes the plugin's stdin. The exit
/// watcher restarts only a process that is still attached when it exits, so a wanted stop is never taken for a crash.
/// </remarks>
public sealed class PluginInstance
{
    private const string KeptStoppingHint = "Save the settings or restart the daemon to try again.";

    private readonly PluginLaunch _launch;
    private readonly PluginTrustStore _trust;
    private readonly ILog _log;
    private readonly HttpClient _http;
    private readonly PluginHostOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    /// <summary>The host's lifetime, and this instance's own end: a start or a restart runs under it.</summary>
    private readonly CancellationTokenSource _stopping;

    /// <summary>When each restart in the window happened, oldest first.</summary>
    private readonly Queue<DateTimeOffset> _restarts = new();

    private PluginInstanceState _state = PluginInstanceState.Stopped;

    /// <summary>Set while <see cref="PluginInstanceState.Starting"/>.</summary>
    private Task<PluginEndpoint>? _starting;

    /// <summary>Set while <see cref="PluginInstanceState.Ready"/>, and held until it is stopped.</summary>
    private PluginProcess? _process;

    /// <summary>Set while <see cref="PluginInstanceState.Failed"/>.</summary>
    private Exception? _failure;

    private IReadOnlyList<string> _lastOutput = [];
    private int _leases;
    private int _launches;

    /// <summary>Stopped for good by the host; nothing starts again.</summary>
    private bool _ended;

    internal PluginInstance(
        PluginLaunch launch,
        PluginTrustStore trust,
        ILog log,
        HttpClient http,
        PluginHostOptions options,
        TimeProvider time,
        CancellationToken lifetime)
    {
        _launch = launch;
        _trust = trust;
        _log = log;
        _http = http;
        _options = options;
        _time = time;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Key = PluginKey.Of(launch);
    }

    public PluginKey Key { get; }

    public PluginInstanceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process?.Id;
            }
        }
    }

    /// <summary>How many times its program was launched, restarts included; a launch refused for trust does not count.</summary>
    public int Launches
    {
        get
        {
            lock (_gate)
            {
                return _launches;
            }
        }
    }

    /// <summary>The last lines of the running process, or of the last one to run when none does.</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            PluginProcess? process;
            IReadOnlyList<string> last;
            lock (_gate)
            {
                process = _process;
                last = _lastOutput;
            }

            return process?.RecentOutput ?? last;
        }
    }

    internal bool IsLeased
    {
        get
        {
            lock (_gate)
            {
                return _leases > 0;
            }
        }
    }

    /// <summary>
    /// The plugin's endpoint once it is ready: at once when it is, by joining the start under way, or by starting it
    /// once the trust record confirms its program. A failed instance throws why it failed.
    /// </summary>
    /// <param name="ct">Stops only this caller's wait. The start goes on under the host's lifetime, for the others.</param>
    /// <exception cref="PluginUnavailableException">It is not confirmed, could not start, or kept stopping.</exception>
    /// <exception cref="ObjectDisposedException">A sweep or the host's disposal has stopped it for good.</exception>
    public Task<PluginEndpoint> ReadyAsync(CancellationToken ct)
    {
        Task<PluginEndpoint> start;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_ended, this);
            switch (_state)
            {
                case PluginInstanceState.Ready:
                    return Task.FromResult(_process!.Endpoint);
                case PluginInstanceState.Failed:
                    return Task.FromException<PluginEndpoint>(_failure!);
                case PluginInstanceState.Starting:
                    start = _starting!;
                    break;
                default:
                    start = BeginStart(TimeSpan.Zero);
                    break;
            }
        }

        return start.WaitAsync(ct);
    }

    /// <summary>Called under the host's lock. A new lease on a failed instance makes it start afresh: a reload is a retry.</summary>
    internal void AddLease()
    {
        lock (_gate)
        {
            _leases++;
            if (_state == PluginInstanceState.Failed)
            {
                _state = PluginInstanceState.Stopped;
                _failure = null;
                _restarts.Clear();
            }
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            _leases--;
        }
    }

    /// <summary>Stops it for good: cancels a start or a restart under way, and stops the process. The host calls it once.</summary>
    internal async Task StopAsync()
    {
        Task? starting;
        PluginProcess? process;
        lock (_gate)
        {
            _ended = true;
            _state = PluginInstanceState.Stopped;
            starting = _starting;
            process = _process;
            _starting = null;
            _process = null;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (starting is not null)
        {
            try
            {
                await starting.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Only its end matters here: it was cancelled, or it failed and logged why.
            }
        }

        if (process is not null)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    /// <summary>Under the lock: a start on the thread pool, so that the trust check's hashing runs outside the lock.</summary>
    private Task<PluginEndpoint> BeginStart(TimeSpan delay)
    {
        var ct = _stopping.Token;
        var start = Task.Run(() => StartAsync(delay, ct));

        // Its callers may have stopped waiting, and nobody waits for a restart: the failure is logged and kept instead.
        _ = start.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _state = PluginInstanceState.Starting;
        _starting = start;
        return start;
    }

    private async Task<PluginEndpoint> StartAsync(TimeSpan delay, CancellationToken ct)
    {
        PluginProcess process;
        var launched = false;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }

            ThrowUnlessConfirmed();
            lock (_gate)
            {
                _launches++;
            }

            launched = true;
            process = await PluginProcess.StartAsync(_launch, _http, _log, _options.Timings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            lock (_gate)
            {
                _starting = null;
                if (launched && !cancelled && !_ended)
                {
                    _state = PluginInstanceState.Failed;
                    _failure = ex;
                    _lastOutput = ex is PluginUnavailableException unavailable ? unavailable.Output : [];
                }
                else
                {
                    // Cancelled by a stop, or refused before any launch: an unconfirmed program is checked again next time.
                    _state = PluginInstanceState.Stopped;
                }
            }

            if (!cancelled)
            {
                LogFailure(ex);
            }

            throw;
        }

        bool attached;
        lock (_gate)
        {
            attached = !_ended;
            if (attached)
            {
                _process = process;
                _state = PluginInstanceState.Ready;
                _starting = null;
            }
        }

        if (!attached)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException($"Plugin {_launch.PluginId} was stopped while it started.");
        }

        _ = WatchAsync(process);
        return process.Endpoint;
    }

    /// <exception cref="PluginUnavailableException">The user has not confirmed this program, or it changed since.</exception>
    private void ThrowUnlessConfirmed()
    {
        var problem = _trust.Check(_launch.PluginId, _launch.Folder, _launch.Executable) switch
        {
            PluginTrust.Confirmed => null,
            PluginTrust.Changed => $"Plugin {_launch.PluginId} changed since you added it.",
            _ => $"Plugin {_launch.PluginId} has not been confirmed on this computer.",
        };
        if (problem is not null)
        {
            throw new PluginUnavailableException(problem, $"Confirm it with 'dictum plugins add {_launch.Folder}'.");
        }
    }

    /// <summary>Restarts the process if it exits while still attached, that is, unasked; within the budget.</summary>
    private async Task WatchAsync(PluginProcess process)
    {
        var code = await process.Exited.ConfigureAwait(false);
        var output = process.RecentOutput;
        string message;
        lock (_gate)
        {
            if (_process != process)
            {
                // Dictum stopped it, and detached it first.
                return;
            }

            _process = null;
            _lastOutput = output;
            if (TakeRestart())
            {
                BeginStart(_options.Delay);
                message = $"Plugin {_launch.PluginId} (pid {process.Id}) stopped by itself with code {code}; restarting it";
            }
            else
            {
                message = KeptStopping();
                _state = PluginInstanceState.Failed;
                _failure = new PluginUnavailableException(message, KeptStoppingHint, output);
            }
        }

        _log.Warn(message);
        await process.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Under the lock: counts a restart unless <see cref="PluginHostOptions.MaxRestarts"/> already fall in the window.</summary>
    private bool TakeRestart()
    {
        var now = _time.GetUtcNow();
        while (_restarts.TryPeek(out var at) && now - at >= _options.Window)
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

    private string KeptStopping()
    {
        var minutes = _options.Window.TotalMinutes.ToString(CultureInfo.InvariantCulture);
        return $"Plugin {_launch.PluginId} kept stopping; Dictum stopped restarting it after {_options.MaxRestarts} restarts within {minutes} minutes.";
    }

    private void LogFailure(Exception ex)
    {
        if (ex is PluginUnavailableException)
        {
            _log.Warn(ex.Message);
        }
        else
        {
            _log.Warn($"Plugin {_launch.PluginId} could not start: {ex.Message}", ex);
        }
    }
}
