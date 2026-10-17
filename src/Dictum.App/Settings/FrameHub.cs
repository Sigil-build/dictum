using Dictum.Host.Ipc;

namespace Dictum.App.Settings;

/// <summary>
/// The daemon's newest audio frame, for the General page's meter. The supervisor publishes on its own thread, about
/// 50 times a second; the meter reads it on its 20 Hz tick. The overlay keeps its own copy.
/// </summary>
public sealed class FrameHub(TimeProvider time)
{
    private Received? _newest;

    public void Publish(IpcEvent frame)
    {
        if (frame.Rms is { } rms)
        {
            Volatile.Write(ref _newest, new Received(rms, time.GetUtcNow()));
        }
    }

    /// <summary>The newest frame's rms when it arrived within <paramref name="maxAge"/>; otherwise silence.</summary>
    public float Rms(TimeSpan maxAge) =>
        Volatile.Read(ref _newest) is { } newest && time.GetUtcNow() - newest.At <= maxAge ? newest.Rms : 0f;

    private sealed record Received(float Rms, DateTimeOffset At);
}
