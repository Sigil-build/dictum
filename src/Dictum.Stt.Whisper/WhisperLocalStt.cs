using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Stt;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Dictum.Stt.Whisper;

/// <summary>In-process whisper.cpp through Whisper.net, with optional Silero VAD.</summary>
public sealed partial class WhisperLocalStt(string id, WhisperLocalSttConfig config, AppPaths paths, ILog log)
    : ISpeechToText, IDisposable
{
    private static readonly Lock RuntimeGate = new();
    private static WhisperRuntime? _configuredRuntime;

    private readonly Lock _gate = new();
    private WhisperFactory? _factory;
    private WhisperVadFactory? _vadFactory;

    public string Id => id;

    public SttCapabilities Capabilities { get; } = new(LanguageDetection: true, Prompt: true, Streaming: false);

    public string ModelPath => paths.ResolveModel(config.Model);

    /// <summary>The native runtime Whisper.net picked, once a model has been loaded.</summary>
    public static string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    public Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct) =>
        Task.Run(() => TranscribeCoreAsync(clip, request, ct), ct);

    public Task<HealthStatus> CheckAsync(CancellationToken ct)
    {
        if (!File.Exists(ModelPath))
        {
            return Task.FromResult(HealthStatus.Error($"Model not found: {ModelPath}", DownloadHint(config.Model)));
        }

        if (config.Vad && !File.Exists(paths.ResolveModel(config.VadModel)))
        {
            return Task.FromResult(HealthStatus.Error($"VAD model not found: {paths.ResolveModel(config.VadModel)}", VadDownloadHint()));
        }

        try
        {
            _ = Factory();
            var detail = $"{config.Model}, runtime {LoadedRuntime ?? "unknown"}";
            return Task.FromResult(_configuredRuntime is { } active && active != config.Runtime
                ? HealthStatus.Warning(
                    $"{detail} (configured '{config.Runtime.ToString().ToLowerInvariant()}', but this process already uses '{active.ToString().ToLowerInvariant()}')",
                    "The first Whisper provider loaded picks the runtime for the whole process.")
                : HealthStatus.Ok(detail));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(HealthStatus.Error($"Whisper failed to load: {ex.Message}", "Try \"runtime\": \"cpu\" in the provider config."));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _factory?.Dispose();
            _vadFactory?.Dispose();
            _factory = null;
            _vadFactory = null;
        }
    }

    private async Task<Transcript> TranscribeCoreAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        if (clip.SampleRate != AudioClip.WhisperSampleRate)
        {
            throw new ArgumentException($"Whisper needs {AudioClip.WhisperSampleRate} Hz audio, got {clip.SampleRate} Hz.", nameof(clip));
        }

        var watch = Stopwatch.StartNew();
        var samples = clip.Samples.ToArray();
        if (config.Vad)
        {
            samples = KeepSpeech(samples);
            if (samples.Length < AudioClip.WhisperSampleRate / 2)
            {
                log.Info("VAD found no speech");
                return new Transcript(string.Empty, request.Language, watch.Elapsed, LoadedRuntime);
            }
        }

        var builder = Factory().CreateBuilder()
            .WithThreads(config.Threads > 0 ? config.Threads : Math.Clamp(Environment.ProcessorCount, 1, 8));
        var language = IsEnglishOnly(config.Model) ? "en" : request.Language;
        builder = language is null or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            builder = builder.WithPrompt(request.Prompt);
        }

        var text = new StringBuilder();
        string? detected = null;
        await using (var processor = builder.Build())
        {
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                detected ??= segment.Language;
                if (!IsNonSpeechTag(segment.Text))
                {
                    text.Append(segment.Text);
                }
            }
        }

        log.Debug($"Whisper '{id}' ({LoadedRuntime}) took {watch.ElapsedMilliseconds} ms for {clip.Duration.TotalSeconds:F1} s");
        return new Transcript(text.ToString().Trim(), language is null or "auto" ? detected : language, watch.Elapsed, LoadedRuntime);
    }

    /// <summary>English-only models (e.g. ggml-base.en.bin) cannot detect or produce other languages.</summary>
    private static bool IsEnglishOnly(string model) =>
        Path.GetFileName(model).Contains(".en.", StringComparison.OrdinalIgnoreCase);

    private WhisperFactory Factory()
    {
        lock (_gate)
        {
            if (_factory is not null)
            {
                return _factory;
            }

            if (!File.Exists(ModelPath))
            {
                throw new ProviderUnavailableException($"Whisper model not found: {ModelPath}. {DownloadHint(config.Model)}");
            }

            ConfigureRuntime(config.Runtime, log);
            var watch = Stopwatch.StartNew();
            _factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { UseGpu = config.Runtime != WhisperRuntime.Cpu });
            log.Info($"Loaded {config.Model} with runtime {LoadedRuntime} in {watch.ElapsedMilliseconds} ms");
            return _factory;
        }
    }

    private float[] KeepSpeech(float[] samples)
    {
        WhisperVadFactory vadFactory;
        lock (_gate)
        {
            var vadPath = paths.ResolveModel(config.VadModel);
            if (!File.Exists(vadPath))
            {
                throw new ProviderUnavailableException($"VAD model not found: {vadPath}. {VadDownloadHint()}");
            }

            _ = Factory();
            vadFactory = _vadFactory ??= WhisperVadFactory.FromPath(vadPath);
        }

        using var vad = vadFactory.CreateBuilder()
            .WithSpeechPadding(TimeSpan.FromMilliseconds(200))
            .Build();

        var speech = new List<float>(samples.Length);
        foreach (var segment in vad.DetectSpeech(samples))
        {
            var start = Math.Clamp((int)(segment.Start.TotalSeconds * AudioClip.WhisperSampleRate), 0, samples.Length);
            var end = Math.Clamp((int)(segment.End.TotalSeconds * AudioClip.WhisperSampleRate), start, samples.Length);
            speech.AddRange(samples.AsSpan(start, end - start));
        }

        return [.. speech];
    }

    /// <summary>
    /// Sets the runtime probing order once per process; Whisper.net keeps it in static state,
    /// so the first provider to load decides.
    /// </summary>
    private static void ConfigureRuntime(WhisperRuntime runtime, ILog log)
    {
        lock (RuntimeGate)
        {
            if (_configuredRuntime is { } existing)
            {
                if (existing != runtime)
                {
                    log.Info($"Whisper runtime is already '{existing}'; '{runtime}' applies only after a restart.");
                }

                return;
            }

            List<RuntimeLibrary> order = runtime switch
            {
                WhisperRuntime.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
                WhisperRuntime.Vulkan => [RuntimeLibrary.Vulkan],
                WhisperRuntime.Cuda => [RuntimeLibrary.Cuda12, RuntimeLibrary.Cuda],
                _ => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cuda12, RuntimeLibrary.Cuda, RuntimeLibrary.CoreML, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            };
            RuntimeOptions.RuntimeLibraryOrder = order;
            _configuredRuntime = runtime;
        }
    }

    public static string DownloadHint(string model) =>
        $"Download it into the models directory, e.g. https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{Path.GetFileName(model)}";

    public static string VadDownloadHint() =>
        "Download https://huggingface.co/sandrohanea/whisper.net/resolve/v4/vad/ggml-silero-v6.2.0.bin into the models directory, or set \"vad\": false.";

    /// <summary>Whisper marks non-speech as e.g. "[BLANK_AUDIO]", "[Music]" or "(silence)".</summary>
    private static bool IsNonSpeechTag(string segment) => NonSpeechTag().IsMatch(segment);

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\))\s*$")]
    private static partial Regex NonSpeechTag();
}
