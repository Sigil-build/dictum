using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.Integration.Tests;

public class IpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-ipc", Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public IpcTests() => Directory.CreateDirectory(_dir);

    private string SocketPath => Path.Combine(_dir, "d.sock");

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

    [Fact]
    public async Task RequestReachesTheHandlerAndTheAnswerComesBack()
    {
        await using var server = new IpcServer(SocketPath, NullLog.Instance);
        await server.StartAsync((request, _) => Task.FromResult(IpcResponse.Success(Status($"{request.Command}:{request.Profile}"))), _ct);

        var response = await new IpcClient(SocketPath).SendAsync(new IpcRequest("toggle", "dictate-uk"), _ct);

        Assert.True(response.Ok);
        Assert.Equal("toggle:dictate-uk", response.Status!.State);
    }

    [Fact]
    public async Task HandlesSeveralClientsInARow()
    {
        await using var server = new IpcServer(SocketPath, NullLog.Instance);
        var count = 0;
        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status($"{Interlocked.Increment(ref count)}"))), _ct);
        var client = new IpcClient(SocketPath);

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal($"{i}", (await client.SendAsync(new IpcRequest("status"), _ct)).Status!.State);
        }
    }

    [Fact]
    public async Task SecondServerIsRejected()
    {
        await using var first = new IpcServer(SocketPath, NullLog.Instance);
        await first.StartAsync((_, _) => Task.FromResult(IpcResponse.Failure("x")), _ct);
        await using var second = new IpcServer(SocketPath, NullLog.Instance);

        await Assert.ThrowsAsync<DaemonAlreadyRunningException>(() =>
            second.StartAsync((_, _) => Task.FromResult(IpcResponse.Failure("x")), _ct));
    }

    [Fact]
    public async Task ClientWithoutServerReportsDaemonNotRunning() =>
        await Assert.ThrowsAsync<DaemonNotRunningException>(() => new IpcClient(SocketPath).SendAsync(new IpcRequest("status"), _ct));

    [Fact]
    public async Task StaleSocketFileIsReplaced()
    {
        await File.WriteAllTextAsync(SocketPath, "stale", _ct);
        await using var server = new IpcServer(SocketPath, NullLog.Instance);

        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status("fresh"))), _ct);

        Assert.Equal("fresh", (await new IpcClient(SocketPath).SendAsync(new IpcRequest("status"), _ct)).Status!.State);
    }

    [Fact]
    public void LongSocketPathsAreShortenedDeterministically()
    {
        var deep = Path.Combine(Path.GetTempPath(), new string('d', 120), "dictum.sock");

        var shortened = IpcEndpoint.Shorten(deep);

        Assert.True(shortened.Length <= 100, shortened);
        Assert.Equal(shortened, IpcEndpoint.Shorten(deep));
        Assert.NotEqual(shortened, IpcEndpoint.Shorten(deep + "2"));
        Assert.Equal(SocketPath, IpcEndpoint.Shorten(SocketPath));
    }

    [Fact(Timeout = 10_000)]
    public async Task SubscribeStreamsPublishedEventsInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        await using var server = new IpcServer(SocketPath, NullLog.Instance, hub);
        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status("Idle"))), ct);

        await using var subscription = await new IpcClient(SocketPath).SubscribeAsync(ct);
        Assert.True(subscription.Response.Ok);
        Assert.Equal(IpcProtocol.Version, subscription.Response.Status!.Protocol);

        hub.Publish(IpcEvent.ForState(PipelineState.Recording, "uk", "PushToTalk"));
        hub.Publish(IpcEvent.Finished(DictationOutcome.Pasted, null, "uk", 4900, 6100));

        var events = new List<IpcEvent>();
        await foreach (var e in subscription.ReadEventsAsync(ct))
        {
            events.Add(e);
            if (events.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(("state", "Recording"), (events[0].Event, events[0].State));
        Assert.Equal(("finished", "pasted", 6100L), (events[1].Event, events[1].Outcome, events[1].TranscribeMs));
    }

    [Fact(Timeout = 10_000)]
    public async Task HangingUpRemovesTheSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        await using var server = new IpcServer(SocketPath, NullLog.Instance, hub);
        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status("Idle"))), ct);

        var subscription = await new IpcClient(SocketPath).SubscribeAsync(ct);
        Assert.Equal(1, hub.SubscriberCount);
        await subscription.DisposeAsync();

        while (hub.SubscriberCount > 0)
        {
            await Task.Delay(10, ct);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task OverflowingSubscriberIsDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        await using var server = new IpcServer(SocketPath, NullLog.Instance, hub, writeTimeout: TimeSpan.FromMilliseconds(200));
        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status("Idle"))), ct);

        await using var neverReads = await new IpcClient(SocketPath).SubscribeAsync(ct);
        var big = IpcEvent.ForStatus(Status(new string('x', 2000)));
        while (hub.SubscriberCount > 0)
        {
            for (var i = 0; i < 100; i++)
            {
                hub.Publish(big);
            }

            await Task.Delay(20, ct);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task EventsDeliveredBeforeDisposeReachTheClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        var server = new IpcServer(SocketPath, NullLog.Instance, hub);
        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status("Idle"))), ct);
        await using var subscription = await new IpcClient(SocketPath).SubscribeAsync(ct);

        hub.Publish(IpcEvent.Stopping("shutdown"));
        Assert.True(await hub.WhenDeliveredAsync(TimeSpan.FromSeconds(2)));
        await server.DisposeAsync();

        var events = new List<IpcEvent>();
        await foreach (var e in subscription.ReadEventsAsync(ct))
        {
            events.Add(e);
        }

        Assert.Equal(("stopping", "shutdown"), (events.Single().Event, events.Single().Reason));
    }

    private static DaemonStatus Status(string state) => new(state, null, [], "test", "test", Environment.ProcessId);
}
