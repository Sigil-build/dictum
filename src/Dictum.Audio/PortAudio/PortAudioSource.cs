using System.Globalization;
using System.Runtime.InteropServices;
using Dictum.Core.Audio;
using Dictum.Core.Diagnostics;
using static Dictum.Audio.PortAudio.PortAudioNative;

namespace Dictum.Audio.PortAudio;

/// <summary>
/// Microphone capture through PortAudio's blocking read API on a dedicated thread,
/// so no managed callback is ever invoked from a native audio thread.
/// </summary>
public sealed class PortAudioSource(ILog log) : IAudioSource
{
    private const int FramesPerSecond = 50;

    /// <summary>Samples reserved per channel up front, so a dictation this long never regrows its buffer.</summary>
    private const int ReservedSeconds = 30;

    /// <summary>Up to two of the device's channels are captured, so the louder one can be picked.</summary>
    private const int MaxCaptureChannels = 2;

    private readonly Lock _gate = new();
    private Thread? _thread;
    private nint _stream;
    private volatile bool _stopping;
    private Exception? _captureError;
    private List<float>[] _channels = [];
    private int _sampleRate;

    /// <summary>False while a level meter runs: frames only, nothing buffered.</summary>
    private volatile bool _keepSamples = true;

    public event Action<AudioFrame>? FrameCaptured;

    /// <summary>Input devices of the preferred host API (WASAPI, ALSA or Core Audio), default first.</summary>
    public unsafe IReadOnlyList<AudioDevice> ListDevices()
    {
        PortAudioLibrary.EnsureInitialized();
        var hostApi = PreferredHostApi();
        var defaultDevice = DefaultInputDevice(hostApi);
        var devices = new List<AudioDevice>();
        var count = Pa_GetDeviceCount();
        for (var i = 0; i < count; i++)
        {
            var info = Pa_GetDeviceInfo(i);
            if (info is null || info->MaxInputChannels <= 0 || info->HostApi != hostApi)
            {
                continue;
            }

            devices.Add(new AudioDevice(
                Id: i.ToString(CultureInfo.InvariantCulture),
                Name: Marshal.PtrToStringUTF8((nint)info->Name) ?? $"Device {i}",
                HostApi: HostApiName(info->HostApi),
                MaxInputChannels: info->MaxInputChannels,
                DefaultSampleRate: info->DefaultSampleRate,
                IsDefault: i == defaultDevice));
        }

        return [.. devices.OrderByDescending(d => d.IsDefault)];
    }

    /// <param name="deviceId">Null for the default device, a PortAudio index, or (part of) a device name.</param>
    public unsafe Task StartAsync(string? deviceId, CancellationToken ct, bool keepSamples = true)
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            PortAudioLibrary.EnsureInitialized();
            var device = FindDevice(deviceId);
            var index = int.Parse(device.Id, CultureInfo.InvariantCulture);
            var info = Pa_GetDeviceInfo(index);
            _sampleRate = (int)device.DefaultSampleRate;

            // Dictation does not need low latency; a larger buffer avoids dropped chunks on a blocking read.
            var latency = info->DefaultHighInputLatency;
            var channels = ChooseChannelCount(index, info->MaxInputChannels, latency);
            var parameters = InputParameters(index, channels, latency);

            var framesPerBuffer = _sampleRate / FramesPerSecond;
            nint stream;
            Check(Pa_OpenStream(&stream, &parameters, null, _sampleRate, new CULong((nuint)framesPerBuffer), new CULong(0), 0, 0), "open the input stream");
            _stream = stream;
            try
            {
                Check(Pa_StartStream(stream), "start the input stream");
            }
            catch
            {
                CloseStream();
                throw;
            }

            log.Info($"Recording from '{device.Name}' ({device.HostApi}, {_sampleRate} Hz, {channels} ch)");
            _keepSamples = keepSamples;
            _channels = [.. Enumerable.Range(0, channels).Select(_ => new List<float>(keepSamples ? _sampleRate * ReservedSeconds : 0))];
            _captureError = null;
            _stopping = false;
            var handle = stream;
            _thread = new Thread(() => CaptureLoop(handle, channels, framesPerBuffer))
            {
                IsBackground = true,
                Name = "dictum-audio-capture",
            };
            _thread.Start();
        }

        return Task.CompletedTask;
    }

    public Task<AudioClip> StopAsync(CancellationToken ct)
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            if (thread is null)
            {
                return Task.FromResult(AudioClip.Empty);
            }

            _stopping = true;
        }

        return Task.Run(
            () =>
            {
                thread.Join();
                lock (_gate)
                {
                    CloseStream();
                    _thread = null;
                    if (_captureError is { } error)
                    {
                        throw new AudioDeviceException($"Recording failed: {error.Message}");
                    }

                    if (!_keepSamples)
                    {
                        _channels = [];
                        return AudioClip.Empty;
                    }

                    var best = LoudestChannel(_channels);
                    var samples = Resampler.Resample(CollectionsMarshal.AsSpan(_channels[best]), _sampleRate, AudioClip.WhisperSampleRate);
                    _channels = [];
                    return new AudioClip(samples);
                }
            },
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_thread is not null)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (AudioDeviceException)
            {
                // The capture loop has already logged the error, and disposal must not throw.
            }
        }
    }

    private unsafe void CaptureLoop(nint stream, int channels, int framesPerBuffer)
    {
        var buffer = new float[framesPerBuffer * channels];
        var mono = new float[framesPerBuffer];
        var channel = new float[framesPerBuffer];
        var spectrum = new SpectrumAnalyzer(_sampleRate, framesPerBuffer);
        var captured = 0L;
        var overflows = 0;
        try
        {
            while (!_stopping)
            {
                int error;
                fixed (float* p = buffer)
                {
                    error = Pa_ReadStream(stream, p, new CULong((nuint)framesPerBuffer));
                }

                if (error == InputOverflowed)
                {
                    overflows++;
                }
                else if (error != NoError)
                {
                    throw new AudioDeviceException(ErrorText(error));
                }

                SplitChannels(buffer, channels, channel, mono);
                captured += framesPerBuffer;
                FrameCaptured?.Invoke(new AudioFrame(
                    Levels.Rms(mono),
                    spectrum.Analyze(mono),
                    TimeSpan.FromSeconds((double)captured / _sampleRate)));
            }
        }
        catch (Exception ex)
        {
            // Nothing above this thread would catch it; StopAsync reports the error to the caller instead.
            log.Error($"Audio capture stopped: {ex.Message}", ex);
            _captureError = ex;
        }
        finally
        {
            if (overflows > 0)
            {
                log.Warn($"Audio input overflowed {overflows} time(s); parts of the recording were lost");
            }
        }
    }

    /// <summary>
    /// Appends each channel of an interleaved buffer to its own recording and copies the loudest one into
    /// <paramref name="mono"/>. Channels are kept apart: averaging them can halve the level (voice in one channel only)
    /// or cancel it (phase-inverted second channel on some headsets and microphone arrays).
    /// </summary>
    private void SplitChannels(float[] buffer, int channels, float[] channel, float[] mono)
    {
        var loudest = -1f;
        for (var c = 0; c < channels; c++)
        {
            for (var f = 0; f < channel.Length; f++)
            {
                channel[f] = buffer[(f * channels) + c];
            }

            if (_keepSamples)
            {
                _channels[c].AddRange(channel);
            }

            var rms = Levels.Rms(channel);
            if (rms > loudest)
            {
                loudest = rms;
                channel.CopyTo(mono, 0);
            }
        }
    }

    /// <summary>Index of the channel with the most energy over the whole recording.</summary>
    private int LoudestChannel(List<float>[] channels)
    {
        var best = 0;
        var bestRms = -1f;
        for (var c = 0; c < channels.Length; c++)
        {
            var rms = Levels.Rms(CollectionsMarshal.AsSpan(channels[c]));
            log.Debug($"Channel {c}: {Levels.ToDb(rms):F0} dBFS RMS");
            if (rms > bestRms)
            {
                best = c;
                bestRms = rms;
            }
        }

        return best;
    }

    private void CloseStream()
    {
        if (_stream == 0)
        {
            return;
        }

        _ = Pa_StopStream(_stream);
        _ = Pa_CloseStream(_stream);
        _stream = 0;
    }

    private AudioDevice FindDevice(string? deviceId)
    {
        var devices = ListDevices();
        if (devices.Count == 0)
        {
            throw new AudioDeviceException("No microphone found.");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
        }

        return devices.FirstOrDefault(d => d.Id == deviceId)
            ?? devices.FirstOrDefault(d => string.Equals(d.Name, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Name.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AudioDeviceException($"Microphone '{deviceId}' not found. Run 'dictum devices' to list them.");
    }

    /// <summary>The device's own channels (up to two), so the loudest one can be picked; mono only as a fallback.</summary>
    private unsafe int ChooseChannelCount(int device, int maxChannels, double latency)
    {
        foreach (var channels in new[] { Math.Min(MaxCaptureChannels, maxChannels), 1, maxChannels })
        {
            var parameters = InputParameters(device, channels, latency);
            if (Pa_IsFormatSupported(&parameters, null, _sampleRate) == NoError)
            {
                return channels;
            }
        }

        throw new AudioDeviceException($"The microphone does not support 32-bit float capture at {_sampleRate} Hz.");
    }

    private static StreamParameters InputParameters(int device, int channels, double latency) => new()
    {
        Device = device,
        ChannelCount = channels,
        SampleFormat = new CULong((nuint)Float32),
        SuggestedLatency = latency,
    };

    private static int PreferredHostApi()
    {
        var preferred = OperatingSystem.IsWindows() ? HostApiWasapi
            : OperatingSystem.IsMacOS() ? HostApiCoreAudio
            : HostApiAlsa;
        var index = Pa_HostApiTypeIdToHostApiIndex(preferred);
        return index >= 0 ? index : Pa_GetDefaultHostApi();
    }

    private static unsafe int DefaultInputDevice(int hostApi)
    {
        var info = Pa_GetHostApiInfo(hostApi);
        return info is not null && info->DefaultInputDevice >= 0 ? info->DefaultInputDevice : Pa_GetDefaultInputDevice();
    }

    private static unsafe string HostApiName(int hostApi)
    {
        var info = Pa_GetHostApiInfo(hostApi);
        return (info is null ? null : Marshal.PtrToStringUTF8((nint)info->Name)) ?? "unknown";
    }

    private static void Check(int error, string action)
    {
        if (error != NoError)
        {
            throw new AudioDeviceException($"Could not {action}: {ErrorText(error)}");
        }
    }
}
