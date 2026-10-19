namespace Dictum.Testing;

/// <summary>
/// A clock that moves only when the test calls <see cref="Advance"/>. Its timers are the system's, so code under test
/// waits in real time, unless it is made with <c>manualTimers</c>: then a timer fires only when <see cref="Advance"/>
/// reaches its due time.
/// </summary>
public sealed class ManualClock(DateTimeOffset? start = null, bool manualTimers = false) : TimeProvider
{
    private static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start ?? DefaultStart;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (!manualTimers)
        {
            return base.CreateTimer(callback, state, dueTime, period);
        }

        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on and fires each manual timer that came due, once.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt <= _now)];
            foreach (var timer in due)
            {
                if (timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan)
                {
                    timer.DueAt = _now + timer.Period;
                }
                else
                {
                    _timers.Remove(timer);
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = owner._now + dueTime;
                    Period = period;
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
