using System.Collections.Concurrent;
using Dictum.App.Shell;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Shell;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-one", Guid.NewGuid().ToString("N")[..8]);

    public SingleInstanceTests() => Directory.CreateDirectory(_dir);

    private string SocketPath => Path.Combine(_dir, "dictum-gui.sock");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("dictum.sock", "dictum-gui.sock")]
    [InlineData("dictum-1a2b3c4d5e6f.sock", "dictum-1a2b3c4d5e6f-gui.sock")]
    public void TheAppSocketSitsNextToTheDaemonSocket(string daemon, string app) =>
        Assert.Equal(Path.Combine(_dir, app), SingleInstance.SocketPathFor(Path.Combine(_dir, daemon)));

    [Fact]
    public async Task ASecondLaunchHandsItsCommandToTheFirst()
    {
        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();

        await using var running = await SingleInstance.AcquireAsync(SocketPath, first.Enqueue, NullLog.Instance, _ct);
        var other = await SingleInstance.AcquireAsync(SocketPath, second.Enqueue, NullLog.Instance, _ct);
        await SingleInstance.NotifyAsync(SocketPath, SingleInstance.ShowSettings, _ct);

        Assert.NotNull(running);
        Assert.Null(other);
        await Eventually.WaitForAsync(() => first.Contains(SingleInstance.ShowSettings), "the forwarded command");
        Assert.Empty(second);
    }

    [Fact]
    public async Task OnceTheFirstExitsTheNextLaunchTakesOver()
    {
        var first = await SingleInstance.AcquireAsync(SocketPath, _ => { }, NullLog.Instance, _ct);
        await first!.DisposeAsync();

        await using var next = await SingleInstance.AcquireAsync(SocketPath, _ => { }, NullLog.Instance, _ct);

        Assert.NotNull(next);
    }

    [Fact]
    public async Task ASecondLaunchWaitsForTheFirstToListen()
    {
        var commands = new ConcurrentQueue<string>();
        var notify = SingleInstance.NotifyAsync(SocketPath, SingleInstance.ShowSettings, _ct);
        await Task.Delay(300, _ct);

        await using var late = await SingleInstance.AcquireAsync(SocketPath, commands.Enqueue, NullLog.Instance, _ct);
        await notify.WaitAsync(TimeSpan.FromSeconds(5), _ct);

        Assert.Equal([SingleInstance.ShowSettings], commands);
    }

    [Fact]
    public async Task NobodyAnsweringIsReportedAfterTheRetries()
    {
        await Assert.ThrowsAsync<DaemonNotRunningException>(() =>
            SingleInstance.NotifyAsync(SocketPath, SingleInstance.ShowSettings, _ct, TimeSpan.FromMilliseconds(300)));
    }

    // Review focus 3: install.sh calls --quit whether or not Dictum runs.
    [Fact]
    public async Task QuitWithoutAnInstanceReturnsAtOnce()
    {
        var started = DateTime.UtcNow;

        Assert.False(await SingleInstance.QuitAsync(SocketPath, _ct));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    // Review focus 3: the call returns once the running instance has let go of its socket.
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
        }, NullLog.Instance, _ct);

        Assert.True(await SingleInstance.QuitAsync(SocketPath, _ct));
        Assert.False(await SingleInstance.QuitAsync(SocketPath, _ct));
    }

    // Review focus 3: an instance that never lets go does not hang the script.
    [Fact]
    public async Task QuitGivesUpAfterThePatience()
    {
        await using var running = await SingleInstance.AcquireAsync(SocketPath, _ => { }, NullLog.Instance, _ct);

        await Assert.ThrowsAsync<TimeoutException>(() => SingleInstance.QuitAsync(SocketPath, _ct, TimeSpan.FromMilliseconds(500)));
    }
}
