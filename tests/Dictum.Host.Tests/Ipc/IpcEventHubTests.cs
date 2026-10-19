using Dictum.Core.Audio;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Testing;

namespace Dictum.Host.Tests.Ipc;

public class IpcEventHubTests
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(1);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task EventsKeepTheirOrderAndOnlyTheNewestFrameIsKept()
    {
        var hub = new IpcEventHub();
        using var subscriber = hub.Subscribe();

        hub.Publish(IpcEvent.ForState(PipelineState.Recording, "uk", "PushToTalk"));
        for (var i = 0; i < 100; i++)
        {
            hub.Publish(IpcEvent.ForFrame(new AudioFrame(0.01f, new float[24], TimeSpan.FromMilliseconds(20 * i))));
        }

        hub.Publish(IpcEvent.ForState(PipelineState.Transcribing, "uk", "PushToTalk"));

        var batch = await subscriber.NextAsync(_ct);
        Assert.Equal([IpcEventKind.State, IpcEventKind.State, IpcEventKind.Frame], batch.Select(e => e.Event));
        Assert.Equal(1.98, batch[2].T);
    }

    [Fact]
    public void SlowSubscriberNeverBlocksPublish()
    {
        var hub = new IpcEventHub();
        using var stuck = hub.Subscribe();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 10_000; i++)
        {
            hub.Publish(IpcEvent.Stopping("test"));
        }

        Assert.True(watch.ElapsedMilliseconds < 1000);
        Assert.True(stuck.Overflowed);
    }

    [Fact]
    public void DisposingASubscriberRemovesIt()
    {
        var hub = new IpcEventHub();
        var subscriber = hub.Subscribe();

        subscriber.Dispose();
        hub.Publish(IpcEvent.Stopping("test"));

        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task WhenDeliveredWaitsUntilTheWriterHasSentEverything()
    {
        var time = new ManualClock(manualTimers: true);
        var hub = new IpcEventHub(time);
        using var subscriber = hub.Subscribe();
        hub.Publish(IpcEvent.Stopping(StoppingReason.Shutdown));

        Assert.False(await DeliveredInTimeAsync(hub, time));
        await subscriber.NextAsync(_ct);
        Assert.False(await DeliveredInTimeAsync(hub, time));
        subscriber.Written();

        Assert.True(await hub.WhenDeliveredAsync(DeliveryTimeout));
    }

    /// <summary>Waits for delivery while the clock runs past the timeout.</summary>
    private static Task<bool> DeliveredInTimeAsync(IpcEventHub hub, ManualClock time)
    {
        var delivered = hub.WhenDeliveredAsync(DeliveryTimeout);
        time.Advance(DeliveryTimeout);
        return delivered;
    }
}
