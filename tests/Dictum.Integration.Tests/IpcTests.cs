using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.Integration.Tests;

public sealed class IpcTests : IDisposable
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
        server.Start((request, _) => Task.FromResult(IpcResponse.Success(Status($"{request.Command}:{request.Profile}"))));

        var response = await new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Toggle, "dictate-uk"), _ct);

        Assert.True(response.Ok);
        Assert.Equal("toggle:dictate-uk", response.Status!.State);
    }

    [Fact]
    public async Task HandlesSeveralClientsInARow()
    {
        await using var server = new IpcServer(SocketPath, NullLog.Instance);
        var count = 0;
        server.Start((_, _) => Task.FromResult(IpcResponse.Success(Status($"{Interlocked.Increment(ref count)}"))));
        var client = new IpcClient(SocketPath);

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal($"{i}", (await client.SendAsync(new IpcRequest(IpcCommand.Status), _ct)).Status!.State);
        }
    }

    [Fact]
    public async Task SecondServerIsRejected()
    {
        await using var first = new IpcServer(SocketPath, NullLog.Instance);
        first.Start((_, _) => Task.FromResult(IpcResponse.Failure("x")));
        await using var second = new IpcServer(SocketPath, NullLog.Instance);

        Assert.Throws<DaemonAlreadyRunningException>(() =>
            second.Start((_, _) => Task.FromResult(IpcResponse.Failure("x"))));
    }

    [Fact]
    public async Task ClientWithoutServerReportsDaemonNotRunning() =>
        await Assert.ThrowsAsync<DaemonNotRunningException>(() => new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Status), _ct));

    [Fact]
    public async Task StaleSocketFileIsReplaced()
    {
        await File.WriteAllTextAsync(SocketPath, "stale", _ct);
        await using var server = new IpcServer(SocketPath, NullLog.Instance);

        server.Start((_, _) => Task.FromResult(IpcResponse.Success(Status("fresh"))));

        Assert.Equal("fresh", (await new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Status), _ct)).Status!.State);
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
        server.Start((_, _) => Task.FromResult(IpcResponse.Success(Status(nameof(PipelineState.Idle)))));

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

        Assert.Equal((IpcEventKind.State, nameof(PipelineState.Recording)), (events[0].Event, events[0].State));
        Assert.Equal((IpcEventKind.Finished, DictationOutcome.Pasted, 6100L), (events[1].Event, events[1].Outcome, events[1].TranscribeMs));
    }

    [Fact(Timeout = 10_000)]
    public async Task HangingUpRemovesTheSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        await using var server = new IpcServer(SocketPath, NullLog.Instance, hub);
        server.Start((_, _) => Task.FromResult(IpcResponse.Success(Status(nameof(PipelineState.Idle)))));

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
        server.Start((_, _) => Task.FromResult(IpcResponse.Success(Status(nameof(PipelineState.Idle)))));

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
        server.Start((_, _) => Task.FromResult(IpcResponse.Success(Status(nameof(PipelineState.Idle)))));
        await using var subscription = await new IpcClient(SocketPath).SubscribeAsync(ct);

        hub.Publish(IpcEvent.Stopping(StoppingReason.Shutdown));
        Assert.True(await hub.WhenDeliveredAsync(TimeSpan.FromSeconds(2)));
        await server.DisposeAsync();

        var events = new List<IpcEvent>();
        await foreach (var e in subscription.ReadEventsAsync(ct))
        {
            events.Add(e);
        }

        Assert.Equal((IpcEventKind.Stopping, StoppingReason.Shutdown), (events.Single().Event, events.Single().Reason));
    }

    [Fact(Timeout = 10_000)]
    public async Task AHandlerExceptionBecomesAFailureAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new IpcServer(SocketPath, NullLog.Instance);
        server.Start((_, _) => throw new InvalidDataException("broken handler"));

        var response = await new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Status), ct);

        Assert.False(response.Ok);
        Assert.Contains("broken handler", response.Error);
    }

    // PR 5b: the GUI subscribes to a daemon that may still be registering its hotkeys; a status published while the
    // reply is being built must still reach it, or step 8 waits for a change it never hears about.
    [Fact(Timeout = 10_000)]
    public async Task AnEventPublishedWhileTheSubscribeReplyIsBuiltIsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        await using var server = new IpcServer(SocketPath, NullLog.Instance, hub);
        server.Start((request, _) =>
        {
            if (request.Command == IpcCommand.Subscribe)
            {
                hub.Publish(IpcEvent.ForStatus(Status(nameof(PipelineState.Recording))));
            }

            return Task.FromResult(IpcResponse.Success(Status(nameof(PipelineState.Idle))));
        });

        await using var subscription = await new IpcClient(SocketPath).SubscribeAsync(ct);
        await foreach (var e in subscription.ReadEventsAsync(ct))
        {
            Assert.Equal((IpcEventKind.Status, nameof(PipelineState.Recording)), (e.Event, e.Status?.State));
            break;
        }
    }

    // PR 5b: subscribing before the reply must not leave a subscriber behind when the reply is a refusal.
    [Fact(Timeout = 10_000)]
    public async Task ARefusedSubscribeLeavesNoSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new IpcEventHub();
        await using var server = new IpcServer(SocketPath, NullLog.Instance, hub);
        server.Start((_, _) => Task.FromResult(IpcResponse.Failure("No.")));

        var response = await new IpcClient(SocketPath).SendAsync(new IpcRequest(IpcCommand.Subscribe), ct);

        Assert.False(response.Ok);
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task AProbeTellsAnAnsweringDaemonFromASilentOneAndFromNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new IpcClient(SocketPath);
        Assert.Equal(DaemonPresence.NotRunning, (await client.ProbeAsync(ct)).Presence);

        await using (var answering = new IpcServer(SocketPath, NullLog.Instance))
        {
            answering.Start((_, _) => Task.FromResult(IpcResponse.Success(Status(nameof(PipelineState.Idle)))));
            var probe = await client.ProbeAsync(ct);
            Assert.Equal((DaemonPresence.Answering, nameof(PipelineState.Idle)), (probe.Presence, probe.Status?.State));
        }

        await using var silent = new IpcServer(SocketPath, NullLog.Instance);
        silent.Start(async (_, serving) =>
        {
            await Task.Delay(Timeout.Infinite, serving);
            return IpcResponse.Failure("never sent");
        });
        Assert.Equal(DaemonPresence.NotAnswering, (await client.ProbeAsync(ct, TimeSpan.FromMilliseconds(200))).Presence);
    }

    private static DaemonStatus Status(string state) => new(state, null, [], "test", "test", Environment.ProcessId);
}
