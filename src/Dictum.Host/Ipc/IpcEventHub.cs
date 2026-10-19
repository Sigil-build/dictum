using System.Collections.Concurrent;

namespace Dictum.Host.Ipc;

/// <summary>Fans daemon events out to every subscriber, never waiting on one.</summary>
/// <param name="time">The clock <see cref="WhenDeliveredAsync"/> waits on; the system clock by default.</param>
public sealed class IpcEventHub(TimeProvider? time = null)
{
    private static readonly TimeSpan DeliveryPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<IpcSubscriber> _subscribers = [];

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public IpcSubscriber Subscribe()
    {
        var subscriber = new IpcSubscriber(this);
        lock (_gate)
        {
            _subscribers.Add(subscriber);
        }

        return subscriber;
    }

    public void Publish(IpcEvent message)
    {
        IpcSubscriber[] targets;
        lock (_gate)
        {
            targets = [.. _subscribers];
        }

        foreach (var target in targets)
        {
            target.Post(message);
        }
    }

    /// <summary>Waits until every subscriber has been sent everything published so far; false when time ran out.</summary>
    /// <remarks>The daemon calls this after announcing that it stops, so the announcement reaches clients before the socket closes.</remarks>
    public async Task<bool> WhenDeliveredAsync(TimeSpan timeout)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            IpcSubscriber[] targets;
            lock (_gate)
            {
                targets = [.. _subscribers];
            }

            if (targets.All(t => t.IsIdle))
            {
                return true;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(DeliveryPollInterval, _time).ConfigureAwait(false);
        }
    }

    internal void Remove(IpcSubscriber subscriber)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscriber);
        }
    }
}

/// <summary>One subscriber's backlog: every non-frame event in order, plus one slot that holds only the newest frame.</summary>
public sealed class IpcSubscriber : IDisposable
{
    /// <summary>A backlog longer than this means the client stopped reading; further events are dropped and it is disconnected.</summary>
    public const int MaxQueued = 1000;

    private readonly IpcEventHub _hub;
    private readonly ConcurrentQueue<IpcEvent> _events = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private IpcEvent? _frame;
    private int _queued;
    private int _inFlight;

    internal IpcSubscriber(IpcEventHub hub) => _hub = hub;

    public bool Overflowed => Volatile.Read(ref _queued) > MaxQueued;

    /// <summary>Everything posted so far has been taken and written to the client.</summary>
    public bool IsIdle => Volatile.Read(ref _queued) == 0 && Volatile.Read(ref _frame) is null && Volatile.Read(ref _inFlight) == 0;

    public void Post(IpcEvent message)
    {
        if (message.Event == IpcEventKind.Frame)
        {
            Interlocked.Exchange(ref _frame, message);
        }
        else if (Interlocked.Increment(ref _queued) <= MaxQueued)
        {
            _events.Enqueue(message);
        }

        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the next NextAsync takes this message too.
        }
    }

    /// <summary>Waits for news, then returns the queued events in order, followed by the newest frame if there is one.</summary>
    public async Task<IReadOnlyList<IpcEvent>> NextAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);
        var batch = new List<IpcEvent>();
        while (_events.TryDequeue(out var message))
        {
            Interlocked.Decrement(ref _queued);
            batch.Add(message);
        }

        if (Interlocked.Exchange(ref _frame, null) is { } frame)
        {
            batch.Add(frame);
        }

        Volatile.Write(ref _inFlight, batch.Count > 0 ? 1 : 0);
        return batch;
    }

    /// <summary>Called by the writer once the last batch from <see cref="NextAsync"/> went out.</summary>
    public void Written() => Volatile.Write(ref _inFlight, 0);

    public void Dispose() => _hub.Remove(this);
}
