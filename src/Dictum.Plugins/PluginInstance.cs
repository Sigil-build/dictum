using System.Globalization;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Core.Text;

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
/// and restarted within the budget when it exits unasked; a restart that cannot start counts as one more exit. One lock
/// guards the state, and nothing waits under it.
/// </summary>
/// <remarks>
/// A stop that Dictum asks for detaches the process under the lock before it closes the plugin's stdin. The exit
/// watcher restarts only a process that is still attached when it exits, so a wanted stop is never taken for a crash.
/// </remarks>
public sealed class PluginInstance
{
    /// <summary>A new lease retries a failed instance, and a settings save or a daemon restart takes one.</summary>
    private const string RetryHint = "Save the settings or restart the daemon to try again.";

    private readonly PluginLaunch _launch;
    private readonly PluginTrustStore _trust;
    private readonly ILog _log;
    private readonly HttpClient _http;
    private readonly IProcessGroups _groups;
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

    /// <summary>Set while <see cref="PluginInstanceState.Failed"/>: why, and how to try again.</summary>
    private PluginUnavailableException? _failure;

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
        IProcessGroups groups,
        PluginHostOptions options,
        TimeProvider time,
        CancellationToken lifetime)
    {
        _launch = launch;
        _trust = trust;
        _log = log;
        _http = http;
        _groups = groups;
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

    /// <summary>
    /// How many times its program was launched, restarts included. A launch refused for trust, or refused before anything
    /// runs, does not count.
    /// </summary>
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

    /// <summary>Cancelled as it is stopped for good. For the tests, which make that cancel fail.</summary>
    internal CancellationToken Stopping => _stopping.Token;

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
    /// once the trust record confirms its program. A failed instance throws why it failed, and how to try again.
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
                    start = BeginStart(TimeSpan.Zero, restart: false);
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

    /// <summary>
    /// Stops it for good: cancels a start or a restart under way, and stops the process. The host calls it once. A part
    /// that fails does not stop the rest, and its failure is thrown once all of it has run; should more than one part
    /// fail, the last one is.
    /// </summary>
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

        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await EndAsync(starting, process).ConfigureAwait(false);
            }
            finally
            {
                _stopping.Dispose();
            }
        }
    }

    /// <summary>Waits for the start that the stop cancelled to end, and stops the process.</summary>
    private static async Task EndAsync(Task? starting, PluginProcess? process)
    {
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
    }

    /// <summary>Under the lock: a start on the thread pool, so that the trust check's hashing runs outside the lock.</summary>
    /// <remarks>
    /// The start may already run when <c>_state</c> and <c>_starting</c> are set here, after it is queued. That is safe
    /// only under the lock, which every caller holds: the start changes both only under it as well, so even one that
    /// ends at once cannot clear <c>_starting</c> before it is set, which would leave the instance starting for good.
    /// </remarks>
    /// <param name="restart">After an exit nobody asked for: a launch that fails counts against the restart budget.</param>
    private Task<PluginEndpoint> BeginStart(TimeSpan delay, bool restart)
    {
        var ct = _stopping.Token;
        var start = Task.Run(() => StartAsync(delay, restart, ct));

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

    private async Task<PluginEndpoint> StartAsync(TimeSpan delay, bool restart, CancellationToken ct)
    {
        PluginProcess process;
        var confirmed = false;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }

            // A stop that came during the delay, or before this start ran at all, launches nothing.
            ct.ThrowIfCancellationRequested();
            ThrowUnlessConfirmed();
            confirmed = true;

            // Before the count: a launch refused before anything runs is a failure, but no launch.
            PluginProcess.RefuseWhatABatchFileWouldRun(_launch);
            lock (_gate)
            {
                _launches++;
            }

            process = await PluginProcess.StartAsync(_launch, _http, _groups, _log, _options.Timings, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
            string? next = null;
            lock (_gate)
            {
                _starting = null;
                if (!confirmed || cancelled || _ended)
                {
                    // Cancelled by a stop, or refused for trust: an unconfirmed program is checked again next time.
                    _state = PluginInstanceState.Stopped;
                }
                else if (!restart)
                {
                    _state = PluginInstanceState.Failed;
                    _failure = ForLaterCallers(ex);
                    _lastOutput = _failure.Output;
                }
                else
                {
                    next = AfterFailedRestart(ex);
                }
            }

            if (!cancelled)
            {
                LogFailure(ex, restart);
            }

            if (next is not null)
            {
                _log.Warn(next);
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

    /// <exception cref="PluginUnavailableException">The user has not confirmed this program, or one of its files changed since.</exception>
    private void ThrowUnlessConfirmed()
    {
        var trust = _trust.Check(_launch.PluginId, _launch.Folder, _launch.Executable);
        var problem = trust.Trust switch
        {
            PluginTrust.Confirmed => null,

            // A file in a listed folder is named as it is on disk, so its name is spelt out like any text from the folder.
            PluginTrust.Changed => $"Plugin {_launch.PluginId} changed since you added it ({TerminalText.Show(trust.ChangedFile)}).",
            _ => $"Plugin {_launch.PluginId} has not been confirmed on this computer.",
        };
        if (problem is not null)
        {
            // Quoted, so that the command works when pasted, whatever spaces the folder holds.
            throw new PluginUnavailableException(problem, $"Confirm it with 'dictum plugins add \"{TerminalText.Show(_launch.Folder)}\"'.");
        }
    }

    /// <summary>Restarts the process if it exits while still attached, that is, unasked; within the budget.</summary>
    private async Task WatchAsync(PluginProcess process)
    {
        var code = await process.Exited.ConfigureAwait(false);

        // The lines it printed last, which a failure keeps once the restarts run out.
        await process.WaitForLastLinesAsync().ConfigureAwait(false);
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
            message = RestartOrGiveUp(output) ?? $"Plugin {_launch.PluginId} (pid {process.Id}) stopped by itself with code {code}; restarting it";
        }

        _log.Warn(message);
        await process.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Under the lock, after a restart's own launch failed: one more exit within the budget, as the daemon's supervisor
    /// counts a start that fails. Returns what to log.
    /// </summary>
    private string AfterFailedRestart(Exception ex)
    {
        _lastOutput = (ex as PluginUnavailableException)?.Output ?? [];
        return RestartOrGiveUp(_lastOutput) ?? $"Plugin {_launch.PluginId} could not be restarted; trying again";
    }

    /// <summary>
    /// Under the lock: begins a restart after the delay while the budget allows, and returns null; past it, fails the
    /// instance and returns why.
    /// </summary>
    private string? RestartOrGiveUp(IReadOnlyList<string> output)
    {
        if (TakeRestart())
        {
            BeginStart(_options.Delay, restart: true);
            return null;
        }

        var message = KeptStopping();
        _state = PluginInstanceState.Failed;
        _failure = new PluginUnavailableException(message, RetryHint, output);
        return message;
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

    /// <summary>
    /// A failed start as the instance keeps it. Whoever waited for the start got the failure itself; a later caller,
    /// such as Settings' Test long after, also learns how to try again.
    /// </summary>
    private PluginUnavailableException ForLaterCallers(Exception ex)
    {
        if (ex is not PluginUnavailableException unavailable)
        {
            return new PluginUnavailableException($"Plugin {_launch.PluginId} could not start: {TerminalText.Show(ex.Message)}", RetryHint);
        }

        var hint = string.IsNullOrEmpty(unavailable.Hint)
            ? RetryHint
            : $"{unavailable.Hint} Then save the settings or restart the daemon to try again.";
        return new PluginUnavailableException(unavailable.Message, hint, unavailable.Output);
    }

    private string KeptStopping()
    {
        var minutes = _options.Window.TotalMinutes.ToString(CultureInfo.InvariantCulture);
        return $"Plugin {_launch.PluginId} kept stopping; Dictum stopped restarting it after {_options.MaxRestarts} restarts within {minutes} minutes.";
    }

    /// <summary>
    /// Always a warning, since a daemon's warm-up reports a status and throws nothing, so the log is where an unattended
    /// machine shows it. A first start's failure goes with a <see cref="PluginStartFailedException"/>, so that a command that
    /// reports the same failure itself can keep it off its console (<see cref="DictumLog.ConsoleFilter"/>); a restart's
    /// reaches no command, so it carries none. A failure Dictum expects is logged with its hint and the plugin's last
    /// lines, and no stack trace; any other keeps its exception.
    /// </summary>
    private void LogFailure(Exception ex, bool restart)
    {
        if (ex is PluginUnavailableException unavailable)
        {
            if (restart)
            {
                var details = PluginStartFailedException.Details(unavailable);
                _log.Warn(ex.Message);
                foreach (var line in details?.Split('\n') ?? [])
                {
                    _log.Warn($"  {line.TrimEnd('\r')}");
                }
            }
            else
            {
                _log.Warn(ex.Message, new PluginStartFailedException(unavailable));
            }

            return;
        }

        var message = $"Plugin {_launch.PluginId} could not start: {TerminalText.Show(ex.Message)}";
        _log.Warn(message, restart ? ex : new PluginStartFailedException(message, ex));
    }
}
