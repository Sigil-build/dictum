namespace Dictum.Core.Audio;

/// <summary>Mono PCM samples in the range [-1, 1]. Whisper expects 16 kHz.</summary>
public sealed record AudioClip(ReadOnlyMemory<float> Samples, int SampleRate = AudioClip.WhisperSampleRate)
{
    public const int WhisperSampleRate = 16_000;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    public static AudioClip Empty { get; } = new(ReadOnlyMemory<float>.Empty);
}

/// <summary>Level and spectrum of one <see cref="Interval"/> of captured audio, for level meters and the overlay.</summary>
public readonly record struct AudioFrame(float Rms, ReadOnlyMemory<float> Bands, TimeSpan At)
{
    /// <summary>How many frames the capture delivers per second.</summary>
    public const int PerSecond = 50;

    /// <summary>Frame times in IPC events and frame dumps are rounded to the millisecond.</summary>
    private const int TimeDecimals = 3;

    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(1) / PerSecond;

    /// <summary><see cref="At"/> in seconds, as frames travel in JSON.</summary>
    public double AtSeconds => Math.Round(At.TotalSeconds, TimeDecimals);
}

public sealed record AudioDevice(string Id, string Name, string HostApi, int MaxInputChannels, double DefaultSampleRate, bool IsDefault);

public interface IAudioSource : IAsyncDisposable
{
    IReadOnlyList<AudioDevice> ListDevices();

    /// <summary>Starts capturing from the device (null = system default).</summary>
    /// <param name="keepSamples">
    /// False for a level meter: frames are raised as usual, but nothing is kept, and <see cref="StopAsync"/> returns an
    /// empty clip at once.
    /// </param>
    Task StartAsync(string? deviceId, CancellationToken ct, bool keepSamples = true);

    /// <summary>Stops capturing and returns everything recorded, resampled to 16 kHz mono.</summary>
    Task<AudioClip> StopAsync(CancellationToken ct);

    /// <summary>Raised on the capture thread about <see cref="AudioFrame.PerSecond"/> times per second.</summary>
    event Action<AudioFrame>? FrameCaptured;
}
