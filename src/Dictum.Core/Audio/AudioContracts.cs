namespace Dictum.Core.Audio;

/// <summary>Mono PCM samples in the range [-1, 1]. Whisper expects 16 kHz.</summary>
public sealed record AudioClip(ReadOnlyMemory<float> Samples, int SampleRate = AudioClip.WhisperSampleRate)
{
    public const int WhisperSampleRate = 16_000;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    public static AudioClip Empty { get; } = new(ReadOnlyMemory<float>.Empty);
}

/// <summary>Level and spectrum of roughly 20 ms of captured audio, for level meters and the overlay.</summary>
public readonly record struct AudioFrame(float Rms, ReadOnlyMemory<float> Bands, TimeSpan At);

public sealed record AudioDevice(string Id, string Name, string HostApi, int MaxInputChannels, double DefaultSampleRate, bool IsDefault);

public interface IAudioSource : IAsyncDisposable
{
    IReadOnlyList<AudioDevice> ListDevices();

    /// <summary>Starts capturing from the device (null = system default).</summary>
    Task StartAsync(string? deviceId, CancellationToken ct);

    /// <summary>Stops capturing and returns everything recorded, resampled to 16 kHz mono.</summary>
    Task<AudioClip> StopAsync(CancellationToken ct);

    /// <summary>Raised on the capture thread about 50 times per second.</summary>
    event Action<AudioFrame>? FrameCaptured;
}
