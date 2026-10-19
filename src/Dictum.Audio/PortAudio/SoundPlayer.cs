using System.Runtime.InteropServices;
using Dictum.Core.Diagnostics;
using static Dictum.Audio.PortAudio.PortAudioNative;

namespace Dictum.Audio.PortAudio;

public enum SoundCue
{
    Start,
    Stop,
    Error,
}

/// <summary>Plays short state cues; abstracted so the daemon can be tested without audio.</summary>
public interface ISoundPlayer
{
    Task PlayAsync(SoundCue cue, CancellationToken ct);
}

/// <summary>
/// Short synthesised cues on the default output device. Best effort: a missing library or device, or a stream that
/// does not open, is logged and ignored; a stream that fails after opening just stays silent.
/// </summary>
public sealed class SoundPlayer(ILog log) : ISoundPlayer, IDisposable
{
    private const int SampleRate = 48_000;

    private const double Volume = 0.25;

    /// <summary>A 5 ms fade in and out, so a tone starts and stops without a click.</summary>
    private const int MaxFadeSamples = SampleRate / 200;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task PlayAsync(SoundCue cue, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var samples = Synthesize(cue);
            await Task.Run(() => Play(samples), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AudioDeviceException or DllNotFoundException)
        {
            log.Debug($"Could not play the {cue} sound: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Rising two-tone chirp for start, falling for stop, low double beep for errors.</summary>
    public static float[] Synthesize(SoundCue cue)
    {
        (double Frequency, double Seconds)[] tones = cue switch
        {
            SoundCue.Start => [(660, 0.07), (990, 0.09)],
            SoundCue.Stop => [(990, 0.07), (660, 0.09)],
            _ => [(220, 0.12), (0, 0.06), (220, 0.12)],
        };

        var samples = new List<float>();
        foreach (var (frequency, seconds) in tones)
        {
            var count = (int)(seconds * SampleRate);
            var fade = Math.Min(count / 4, MaxFadeSamples);
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, count - 1 - i) / (double)Math.Max(1, fade));
                samples.Add((float)(Volume * envelope * Math.Sin(2 * Math.PI * frequency * i / SampleRate)));
            }
        }

        return [.. samples];
    }

    private static unsafe void Play(float[] samples)
    {
        PortAudioLibrary.EnsureInitialized();
        var device = Pa_GetDefaultOutputDevice();
        var info = device >= 0 ? Pa_GetDeviceInfo(device) : null;
        if (info is null)
        {
            throw new AudioDeviceException("No output device.");
        }

        var parameters = new StreamParameters
        {
            Device = device,
            ChannelCount = 1,
            SampleFormat = new CULong((nuint)Float32),
            SuggestedLatency = info->DefaultLowOutputLatency,
        };

        nint stream;
        var error = Pa_OpenStream(&stream, null, &parameters, SampleRate, new CULong(0), new CULong(0), 0, 0);
        if (error != NoError)
        {
            throw new AudioDeviceException(ErrorText(error));
        }

        // A cue is not worth an error once its stream is open, so the results below are not checked.
        try
        {
            _ = Pa_StartStream(stream);
            fixed (float* data = samples)
            {
                _ = Pa_WriteStream(stream, data, new CULong((nuint)samples.Length));
            }

            // Pa_StopStream waits for queued audio to finish playing.
            _ = Pa_StopStream(stream);
        }
        finally
        {
            _ = Pa_CloseStream(stream);
        }
    }
}
