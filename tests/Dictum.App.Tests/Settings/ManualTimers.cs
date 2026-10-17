using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

/// <summary>Timers the test fires by hand.</summary>
internal sealed class ManualTimers : ITimers
{
    private readonly List<Entry> _entries = [];

    public int Pending => _entries.Count(e => !e.Stopped);

    public TimeSpan? LastDelay { get; private set; }

    public IDisposable Once(TimeSpan delay, Action action) => Add(delay, action, repeat: false);

    public IDisposable Every(TimeSpan interval, Action action) => Add(interval, action, repeat: true);

    /// <summary>Runs every live timer once; one-shot timers then stop.</summary>
    public void Fire()
    {
        foreach (var entry in _entries.Where(e => !e.Stopped).ToList())
        {
            if (!entry.Repeat)
            {
                entry.Stopped = true;
            }

            entry.Action();
        }
    }

    private Entry Add(TimeSpan delay, Action action, bool repeat)
    {
        LastDelay = delay;
        var entry = new Entry(action, repeat);
        _entries.Add(entry);
        return entry;
    }

    private sealed class Entry(Action action, bool repeat) : IDisposable
    {
        public Action Action { get; } = action;

        public bool Repeat { get; } = repeat;

        public bool Stopped { get; set; }

        public void Dispose() => Stopped = true;
    }
}
