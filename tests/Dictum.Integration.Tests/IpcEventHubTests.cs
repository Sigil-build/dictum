using Dictum.Core.Audio;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

namespace Dictum.Integration.Tests;

public class IpcEventHubTests
{
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
        Assert.Equal(["state", "state", "frame"], batch.Select(e => e.Event));
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
}
