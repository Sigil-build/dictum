using Dictum.Core.Audio;

namespace Dictum.Audio;

/// <summary>
/// Stands in for the microphone with a WAV file (<c>DICTUM_AUDIO_FILE</c>), for testing the daemon and
/// hotkeys without speaking. Every recording returns the whole file.
/// </summary>
public sealed class FileAudioSource(string path) : IAudioSource
{
    public event Action<AudioFrame>? FrameCaptured;

    public IReadOnlyList<AudioDevice> ListDevices() =>
        [new AudioDevice("file", Path.GetFileName(path), "file", MaxInputChannels: 1, AudioClip.WhisperSampleRate, IsDefault: true)];

    public Task StartAsync(string? deviceId, CancellationToken ct, bool keepSamples = true)
    {
        FrameCaptured?.Invoke(new AudioFrame(0, ReadOnlyMemory<float>.Empty, TimeSpan.Zero));
        return Task.CompletedTask;
    }

    public Task<AudioClip> StopAsync(CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        return Task.FromResult(Wav.ReadClip(stream));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
