using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Core.Stt;
using Dictum.Host.Ipc;

namespace Dictum.Host.Daemon;

/// <summary>Runs <c>test-stt</c>: one provider transcribes a few seconds from the microphone, or the built-in sample.</summary>
/// <param name="gate">The daemon's gate, which a dictation, the level meter and an engine swap also hold.</param>
/// <param name="ended">Runs after each test, so a config reload that waited for it is applied.</param>
internal sealed class SttTester(SemaphoreSlim gate, Func<IDictationEngine> engine, LevelMonitor monitor, Func<Task> ended)
{
    /// <summary>Must match the LogicalName of the sample in Dictum.Host.csproj.</summary>
    private const string SampleResource = "Dictum.Host.Samples.uk.wav";

    private const int MinTestSeconds = 1;
    private const int MaxTestSeconds = 30;

    private volatile bool _testing;

    /// <summary>A test holds off dictations and config reloads, so its engine stays alive; the capture thread reads it too.</summary>
    public bool IsTesting => _testing;

    public async Task<SttTestResult> TestAsync(IpcRequest request, CancellationToken ct)
    {
        var id = request.Stt ?? throw new ConfigException("Name the speech provider to test.");
        var sample = request.Sample == true;
        IDictationEngine tested;
        ISpeechToText stt;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            tested = engine();
            if (_testing || (!sample && (tested.Pipeline.State != PipelineState.Idle || monitor.IsMonitoring)))
            {
                throw new InvalidOperationException("Dictum is busy; test the provider when the dictation or level meter has finished.");
            }

            stt = tested.GetStt(id);
            if (!sample)
            {
                await tested.Audio.StartAsync(tested.Config.General.AudioDevice, ct).ConfigureAwait(false);
            }

            _testing = true;
        }
        finally
        {
            gate.Release();
        }

        try
        {
            var clip = sample ? LoadSample() : await RecordAsync(tested, request.Seconds, ct).ConfigureAwait(false);
            var language = tested.Config.Profiles.FirstOrDefault(p => p.Stt == id)?.Language;
            var watch = Stopwatch.StartNew();
            var transcript = await stt.TranscribeAsync(clip, SttRequest.ForSetting(language, null) ?? new SttRequest(null, null), ct).ConfigureAwait(false);
            return new SttTestResult(transcript.Text.Trim(), transcript.Language, watch.ElapsedMilliseconds, transcript.Backend, transcript.Model,
                clip.Duration.TotalSeconds);
        }
        finally
        {
            _testing = false;
            await ended().ConfigureAwait(false);
        }
    }

    private static async Task<AudioClip> RecordAsync(IDictationEngine engine, int? seconds, CancellationToken ct)
    {
        AudioClip clip;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds ?? IpcProtocol.DefaultTestSeconds, MinTestSeconds, MaxTestSeconds)), ct).ConfigureAwait(false);
        }
        finally
        {
            // Also when the wait is cancelled: the microphone is never left open.
            clip = await engine.Audio.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return clip;
    }

    /// <summary>6.9 s of Ukrainian speech ("тестовий запис"), synthesised with Piper TTS from a CC0 dataset.</summary>
    private static AudioClip LoadSample()
    {
        using var stream = typeof(SttTester).Assembly.GetManifestResourceStream(SampleResource)
            ?? throw new InvalidOperationException("The built-in speech sample is missing from this build.");
        return Wav.ReadClip(stream);
    }
}
