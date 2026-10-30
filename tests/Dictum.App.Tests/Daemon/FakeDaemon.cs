using System.Collections.Concurrent;
using Dictum.App.Daemon;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Daemon;

/// <param name="Protocol">What its status reports; another number plays a daemon from another version.</param>
/// <param name="Subscribes">False plays a v0.1/v0.2 daemon, which answers <c>subscribe</c> with an error.</param>
/// <param name="StopsOnShutdown">False plays a daemon that does not know <c>shutdown</c>.</param>
internal sealed record FakeDaemonOptions(int ProcessId = 4242, int Protocol = IpcProtocol.Version, bool Subscribes = true, bool StopsOnShutdown = true);

/// <summary>A daemon stand-in on a real socket: Host's IPC server and event hub with a fixed status.</summary>
internal sealed class FakeDaemon : IAsyncDisposable
{
    private readonly IpcServer _server;
    private readonly FakeDaemonOptions _options;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _ended;

    private FakeDaemon(string socketPath, FakeDaemonOptions options)
    {
        _options = options;
        _server = new IpcServer(socketPath, NullLog.Instance, Events);
    }

    public IpcEventHub Events { get; } = new();

    public ConcurrentQueue<string> Commands { get; } = new();

    public int ProcessId => _options.ProcessId;

    public Task<int> Exited => _exited.Task;

    public bool Paused { get; set; }

    /// <summary><see cref="ExitAsync"/> has begun; <see cref="Exited"/> completes once the socket and its lock are gone.</summary>
    public bool Ending => Volatile.Read(ref _ended) == 1;

    public static FakeDaemon Start(string socketPath, FakeDaemonOptions? options = null)
    {
        var daemon = new FakeDaemon(socketPath, options ?? new FakeDaemonOptions());

        daemon._server.Start(daemon.HandleAsync);
        return daemon;
    }

    public DaemonStatus Status() => new(
        nameof(PipelineState.Idle),
        null,
        [new ProfileStatus("uk", "Ctrl+Alt+Space", "PushToTalk", true)],
        "fake hotkeys",
        "fake paste",
        _options.ProcessId,
        Paused: Paused,
        Protocol: _options.Protocol);

    /// <summary>Ends the way a process does: the socket closes and <see cref="Exited"/> completes.</summary>
    public async Task ExitAsync(int code = 1)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1)
        {
            return;
        }

        await _server.DisposeAsync();
        _exited.TrySetResult(code);
    }

    public ValueTask DisposeAsync() => new(ExitAsync(0));

    private Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        Commands.Enqueue(request.Command);
        switch (request.Command)
        {
            case IpcCommand.Subscribe when !_options.Subscribes:
            case IpcCommand.Shutdown when !_options.StopsOnShutdown:
                return Task.FromResult(IpcResponse.Failure($"Unknown command '{request.Command}'."));
            case IpcCommand.Shutdown:
                Events.Publish(IpcEvent.Stopping(StoppingReason.Shutdown));
                _ = Task.Run(async () =>
                {
                    await Events.WhenDeliveredAsync(TimeSpan.FromSeconds(1));
                    await ExitAsync(0);
                }, CancellationToken.None);
                break;
        }

        return Task.FromResult(IpcResponse.Success(Status()));
    }
}

internal enum LaunchOutcome
{
    Works,
    ExitsAtOnce,
    NeverListens,
}

/// <summary>Launches fake daemons on the test's socket; pids start at 1001.</summary>
internal sealed class FakeLauncher(string socketPath) : IDaemonLauncher, IAsyncDisposable
{
    private static readonly TimeSpan EndingTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<int, FakeDaemon> _daemons = new();
    private int _launches;

    public int Launches => Volatile.Read(ref _launches);

    public Func<int, LaunchOutcome> Outcome { get; set; } = _ => LaunchOutcome.Works;

    public DaemonLaunchException? Refuse { get; set; }

    public FakeDaemon? Current { get; private set; }

    public ConcurrentQueue<int> Ended { get; } = new();

    /// <summary>Thrown by <see cref="EndAsync"/>: an ending that fails in a way the supervisor did not foresee.</summary>
    public Exception? EndFails { get; set; }

    /// <summary>A daemon the test started itself, so that <see cref="EndAsync"/> can end it.</summary>
    public void Adopt(FakeDaemon daemon) => _daemons[daemon.ProcessId] = daemon;

    public IDaemonProcess Launch()
    {
        if (Refuse is not null)
        {
            throw Refuse;
        }

        var launch = Interlocked.Increment(ref _launches);
        var pid = 1000 + launch;
        switch (Outcome(launch))
        {
            case LaunchOutcome.ExitsAtOnce:
                return new FakeProcess(pid, Task.FromResult(2));
            case LaunchOutcome.NeverListens:
                return new FakeProcess(pid, new TaskCompletionSource<int>().Task);
            default:
                // A daemon process that goes away lets go of its socket and its lock at once. A fake one closes its
                // connections before it releases the lock, and a supervisor quick to start the next one would meet it.
                foreach (var ending in _daemons.Values.Where(d => d.Ending))
                {
                    ending.Exited.Wait(EndingTimeout);
                }

                var daemon = FakeDaemon.Start(socketPath, new FakeDaemonOptions(ProcessId: pid));
                _daemons[pid] = daemon;
                Current = daemon;
                return new FakeProcess(pid, daemon.Exited);
        }
    }

    public async Task EndAsync(int processId, TimeSpan grace, CancellationToken ct)
    {
        if (EndFails is not null)
        {
            throw EndFails;
        }

        Ended.Enqueue(processId);
        if (_daemons.TryGetValue(processId, out var daemon))
        {
            await Task.WhenAny(daemon.Exited, Task.Delay(grace, ct));
            await daemon.ExitAsync(137);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var daemon in _daemons.Values)
        {
            await daemon.DisposeAsync();
        }
    }

    private sealed class FakeProcess(int id, Task<int> exited) : IDaemonProcess
    {
        public int Id => id;

        public Task<int> Exited => exited;

        public void Dispose()
        {
        }
    }
}
