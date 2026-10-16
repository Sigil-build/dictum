namespace Dictum.App.Tests;

/// <summary>A clock the test moves by hand. Only <see cref="GetUtcNow"/> is overridden: code under test reads the time
/// from it but waits with real delays.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
