using Dictum.Core.Audio;

namespace Dictum.Testing;

/// <summary>
/// A microphone that hears nothing: each start raises one silent frame, each stop returns the clip it was made with
/// (one second of silence by default), and <see cref="Raise"/> sends the frames a test wants.
/// </summary>
public sealed class FakeAudioSource(AudioClip? clip = null) : IAudioSource
{
    private readonly AudioClip _clip = clip ?? new AudioClip(new float[AudioClip.WhisperSampleRate]);

    public event Action<AudioFrame>? FrameCaptured;

    public IReadOnlyList<AudioDevice> Devices { get; set; } = [];

    public Exception? FailWith { get; set; }

    public Exception? FailOnStop { get; set; }

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    /// <summary>True from a start until the stop that ends it.</summary>
    public bool Started { get; private set; }

    public bool Stopped => Stops > 0;

    public bool? LastKeepSamples { get; private set; }

    public IReadOnlyList<AudioDevice> ListDevices() => Devices;

    public Task StartAsync(string? deviceId, CancellationToken ct, bool keepSamples = true)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Starts++;
        Started = true;
        LastKeepSamples = keepSamples;
        FrameCaptured?.Invoke(default);
        return Task.CompletedTask;
    }

    /// <summary>Like PortAudioSource, a stop with a cancelled token never runs, and the microphone stays held.</summary>
    public Task<AudioClip> StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailOnStop is not null)
        {
            throw FailOnStop;
        }

        Stops++;
        Started = false;
        return Task.FromResult(_clip);
    }

    public void Raise(AudioFrame frame) => FrameCaptured?.Invoke(frame);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
