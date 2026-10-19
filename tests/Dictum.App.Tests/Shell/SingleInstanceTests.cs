using System.Collections.Concurrent;
using System.Net.Sockets;
using Dictum.App.Shell;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Shell;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TempDirectory _dir = new();

    private string SocketPath => _dir.File("dictum-gui.sock");

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("dictum.sock", "dictum-gui.sock")]
    [InlineData("dictum-1a2b3c4d5e6f.sock", "dictum-1a2b3c4d5e6f-gui.sock")]
    public void TheAppSocketSitsNextToTheDaemonSocket(string daemon, string app) =>
        Assert.Equal(_dir.File(app), SingleInstance.SocketPathFor(_dir.File(daemon)));

    [Fact]
    public async Task ASecondLaunchHandsItsCommandToTheFirst()
    {
        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();

        await using var running = await SingleInstance.AcquireAsync(SocketPath, first.Enqueue, NullLog.Instance);
        var other = await SingleInstance.AcquireAsync(SocketPath, second.Enqueue, NullLog.Instance);
        await SingleInstance.NotifyAsync(SocketPath, SingleInstance.ShowSettings, _ct);

        Assert.NotNull(running);
        Assert.Null(other);
        await Eventually.WaitForAsync(() => first.Contains(SingleInstance.ShowSettings), "the forwarded command");
        Assert.Empty(second);
    }

    [Fact]
    public async Task OnceTheFirstExitsTheNextLaunchTakesOver()
    {
        var first = await SingleInstance.AcquireAsync(SocketPath, _ => { }, NullLog.Instance);
        await first!.DisposeAsync();

        await using var next = await SingleInstance.AcquireAsync(SocketPath, _ => { }, NullLog.Instance);

        Assert.NotNull(next);
    }

    [Fact]
    public async Task ASecondLaunchWaitsForTheFirstToListen()
    {
        var commands = new ConcurrentQueue<string>();
        var notify = SingleInstance.NotifyAsync(SocketPath, SingleInstance.ShowSettings, _ct);
        await Task.Delay(300, _ct);

        await using var late = await SingleInstance.AcquireAsync(SocketPath, commands.Enqueue, NullLog.Instance);
        await notify.WaitAsync(TimeSpan.FromSeconds(5), _ct);

        Assert.Equal([SingleInstance.ShowSettings], commands);
    }

    [Fact]
    public async Task NobodyAnsweringIsReportedAfterTheRetries()
    {
        await Assert.ThrowsAsync<DaemonNotRunningException>(() =>
            SingleInstance.NotifyAsync(SocketPath, SingleInstance.ShowSettings, _ct, TimeSpan.FromMilliseconds(300)));
    }

    // install.sh calls --quit whether or not Dictum runs.
    [Fact]
    public async Task QuitWithoutAnInstanceReturnsAtOnce()
    {
        var started = DateTime.UtcNow;

        Assert.False(await SingleInstance.QuitAsync(SocketPath, _ct));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    // The call returns once the running instance has let go of its socket.
    [Fact]
    public async Task QuitWaitsUntilTheInstanceIsGone()
    {
        SingleInstance? running = null;
        running = await SingleInstance.AcquireAsync(SocketPath, command =>
        {
            if (command == SingleInstance.Quit)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    await running!.DisposeAsync();
                });
            }
        }, NullLog.Instance);

        Assert.True(await SingleInstance.QuitAsync(SocketPath, _ct));
        Assert.False(await SingleInstance.QuitAsync(SocketPath, _ct));
    }

    // An instance that never lets go does not hang the script.
    [Fact]
    public async Task QuitGivesUpAfterThePatience()
    {
        await using var running = await SingleInstance.AcquireAsync(SocketPath, _ => { }, NullLog.Instance);

        await Assert.ThrowsAsync<TimeoutException>(() => SingleInstance.QuitAsync(SocketPath, _ct, TimeSpan.FromMilliseconds(500)));
    }

    // An instance that holds the socket but never answers (hung) must not crash --quit; it gives up.
    [Fact]
    public async Task QuitOfASilentInstanceGivesUpInsteadOfThrowing()
    {
        using var silent = Listen(SocketPath);

        await Assert.ThrowsAsync<TimeoutException>(() => SingleInstance.QuitAsync(SocketPath, _ct, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task QuitExitsZeroWithoutAnInstanceAndOneForAHungOne()
    {
        var error = new StringWriter();
        Assert.Equal(0, await SingleInstance.QuitCommandAsync(SocketPath, DaemonSocket, error, _ct));

        using var silent = Listen(SocketPath);
        Assert.Equal(1, await SingleInstance.QuitCommandAsync(SocketPath, DaemonSocket, error, _ct, TimeSpan.FromMilliseconds(500)));
        Assert.Contains("still running", error.ToString());
    }

    // The shell asks its daemon to stop and exits; --quit returns only once no Dictum process from this folder runs, so
    // an upgrade never meets dictum.exe still exiting.
    [Fact]
    public async Task QuitWaitsForTheDaemonToo()
    {
        var processes = new FakeProcesses(4242);
        await using var daemon = new FakeDaemon(processes, 4242, exitsOnShutdown: false);
        daemon.Start(DaemonSocket);
        SingleInstance? running = null;
        running = await SingleInstance.AcquireAsync(SocketPath, command =>
        {
            if (command == SingleInstance.Quit)
            {
                _ = Task.Run(async () =>
                {
                    await running!.DisposeAsync();
                    await Task.Delay(600);
                    processes.Exit(4242);
                });
            }
        }, NullLog.Instance);

        var code = await SingleInstance.QuitCommandAsync(SocketPath, DaemonSocket, _dir.Path, processes, TextWriter.Null, _ct);

        Assert.Equal((0, false), (code, processes.IsRunning(4242)));
    }

    // A daemon from this folder that runs without the desktop app (a login item, a terminal) is stopped too, or the
    // installer meets its files in use.
    [Fact]
    public async Task QuitStopsALoneDaemonFromThisFolder()
    {
        var processes = new FakeProcesses(4242);
        await using var daemon = new FakeDaemon(processes, 4242, exitsOnShutdown: true);
        daemon.Start(DaemonSocket);

        var code = await SingleInstance.QuitCommandAsync(SocketPath, DaemonSocket, _dir.Path, processes, TextWriter.Null, _ct);

        Assert.Equal((0, true, false), (code, daemon.ShutdownAsked, processes.IsRunning(4242)));
    }

    // A daemon from another copy (a portable folder) answers on the same socket; it is not ours to stop.
    [Fact]
    public async Task QuitLeavesADaemonFromAnotherFolderAlone()
    {
        var processes = new FakeProcesses();
        await using var daemon = new FakeDaemon(processes, 999, exitsOnShutdown: true);
        daemon.Start(DaemonSocket);

        var code = await SingleInstance.QuitCommandAsync(SocketPath, DaemonSocket, _dir.Path, processes, TextWriter.Null, _ct);

        Assert.Equal((0, false), (code, daemon.ShutdownAsked));
    }

    // A Dictum process from this folder that does not exit is reported, never waited for forever.
    [Fact]
    public async Task QuitReportsAProcessThatDoesNotExit()
    {
        var processes = new FakeProcesses(4242);
        await using var daemon = new FakeDaemon(processes, 4242, exitsOnShutdown: false);
        daemon.Start(DaemonSocket);
        var error = new StringWriter();

        var code = await SingleInstance.QuitCommandAsync(SocketPath, DaemonSocket, _dir.Path, processes, error, _ct, TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, code);
        Assert.Contains("still running", error.ToString());
    }

    private string DaemonSocket => _dir.File("dictum.sock");

    /// <summary>A socket that takes connections and never answers: a hung instance.</summary>
    private static Socket Listen(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen();
        return socket;
    }

    /// <summary>The Dictum processes of the test's install folder, as --quit sees them.</summary>
    private sealed class FakeProcesses(params int[] running) : IDictumProcesses
    {
        private readonly ConcurrentDictionary<int, bool> _running = new(running.Select(pid => KeyValuePair.Create(pid, true)));

        public IReadOnlyList<int> RunningFrom(string folder) => [.. _running.Keys];

        public bool IsRunning(int pid) => _running.ContainsKey(pid);

        public void Exit(int pid) => _running.TryRemove(pid, out _);
    }

    /// <summary>A daemon socket that reports <paramref name="pid"/> and records a shutdown request.</summary>
    private sealed class FakeDaemon(FakeProcesses processes, int pid, bool exitsOnShutdown) : IAsyncDisposable
    {
        private IpcServer? _server;

        public bool ShutdownAsked { get; private set; }

        public void Start(string socket)
        {
            _server = new IpcServer(socket, NullLog.Instance);
            _server.Start((request, _) =>
            {
                if (request.Command == IpcCommand.Shutdown)
                {
                    ShutdownAsked = true;
                    if (exitsOnShutdown)
                    {
                        processes.Exit(pid);
                    }
                }

                return Task.FromResult(IpcResponse.Success(new DaemonStatus(nameof(PipelineState.Idle), null, [], "hook", "paste", pid)));
            });
        }

        public ValueTask DisposeAsync() => _server?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
