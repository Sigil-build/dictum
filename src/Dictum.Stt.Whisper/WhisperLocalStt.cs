using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dictum.Audio;
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
    /// <summary>Upper bound of the default thread count, which follows the CPU count.</summary>
    private const int MaxDefaultThreads = 8;

    /// <summary>Half a second; less speech than this after VAD is treated as none.</summary>
    private const int MinSpeechSamples = AudioClip.WhisperSampleRate / 2;

    private const string EnglishCode = "en";

    /// <summary>Kept around each speech segment VAD finds, so word edges are not clipped.</summary>
    private static readonly TimeSpan VadSpeechPadding = TimeSpan.FromMilliseconds(200);

    private static readonly Lazy<HashSet<string>> Supported = new(() => [.. WhisperFactory.GetSupportedLanguages()]);

    /// <summary>Country codes people type for a language, with the language code Whisper expects.</summary>
    private static readonly Dictionary<string, string> CommonSlips = new(StringComparer.Ordinal)
    {
        ["ua"] = "uk", ["by"] = "be", ["cz"] = "cs", ["dk"] = "da", ["se"] = "sv", ["gr"] = "el",
        ["jp"] = "ja", ["kr"] = "ko", ["cn"] = "zh", ["ee"] = "et", ["rs"] = "sr", ["ir"] = "fa",
    };

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
            if (LoadedRuntime == nameof(RuntimeLibrary.Vulkan) && !VulkanProbe.HasHardwareGpu)
            {
                return Task.FromResult(HealthStatus.Warning(
                    $"{detail} on a software Vulkan device ({string.Join(", ", VulkanProbe.Devices)})",
                    "This is slow and can produce wrong text; set \"runtime\": \"cpu\"."));
            }

            if (LoadedRuntime == nameof(RuntimeLibrary.Vulkan))
            {
                detail += $" on {string.Join(", ", VulkanProbe.Devices.Where(d => d.IsHardware))}";
            }

            return Task.FromResult(_configuredRuntime is { } active && active != config.Runtime && !Satisfies(config.Runtime, LoadedRuntime)
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
            if (samples.Length < MinSpeechSamples)
            {
                log.Info("VAD found no speech");
                return new Transcript(string.Empty, request.Language, watch.Elapsed, LoadedRuntime, Path.GetFileName(config.Model));
            }
        }

        var factory = Factory();
        var candidates = LanguageCandidates(request);
        var language = IsEnglishOnly(config.Model) ? EnglishCode : request.Language ?? candidates?[0];
        var text = new StringBuilder();
        string? detected = null;
        var processor = CreateBuilder(factory, language, request.Prompt).Build();
        await using (processor.ConfigureAwait(false))
        {
            if (candidates is not null)
            {
                language = await PickLanguageAsync(processor, samples, candidates, ct).ConfigureAwait(false);
                processor.ChangeLanguage(language);
            }

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
        return new Transcript(text.ToString().Trim(), language is null or LanguageList.Auto ? detected : language, watch.Elapsed, LoadedRuntime, Path.GetFileName(config.Model));
    }

    /// <summary>The profile's language list when Whisper is to pick from it, otherwise null.</summary>
    private IReadOnlyList<string>? LanguageCandidates(SttRequest request)
    {
        var candidates = !IsEnglishOnly(config.Model) && request is { Language: null, Candidates.Count: > 1 } ? request.Candidates : null;
        if (candidates is not null && UnknownCandidate(candidates, Supported.Value) is { } unknown)
        {
            // Whisper skips a code it does not know, so "ua, en" would silently turn Ukrainian speech into English.
            throw new ProviderUnavailableException(unknown);
        }

        return candidates;
    }

    private WhisperProcessorBuilder CreateBuilder(WhisperFactory factory, string? language, string? prompt)
    {
        var builder = factory.CreateBuilder()
            .WithThreads(config.Threads > 0 ? config.Threads : Math.Clamp(Environment.ProcessorCount, 1, MaxDefaultThreads));
        builder = language is null or LanguageList.Auto ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        return string.IsNullOrWhiteSpace(prompt) ? builder : builder.WithPrompt(prompt);
    }

    /// <summary>A message naming the first candidate Whisper does not know, or null when it knows them all.</summary>
    public static string? UnknownCandidate(IReadOnlyList<string> candidates, IReadOnlyCollection<string> supported)
    {
        var unknown = candidates.FirstOrDefault(c => !supported.Contains(c));
        return unknown is null
            ? null
            : CommonSlips.TryGetValue(unknown, out var code) && supported.Contains(code)
                ? $"'{unknown}' in the profile's language list is a country code; the language code Whisper knows is '{code}'."
                : $"'{unknown}' in the profile's language list is not a language Whisper knows.";
    }

    /// <summary>The most likely of <paramref name="candidates"/>; the first one when the model names none of them.</summary>
    private async Task<string> PickLanguageAsync(WhisperProcessor processor, float[] samples, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var (detected, probability) = await Task.Run(() => processor.DetectLanguageWithProbability(samples, [.. candidates]), ct).ConfigureAwait(false);
        var picked = detected is not null && candidates.Contains(detected) ? detected : candidates[0];
        log.Debug($"Whisper '{id}' picked '{picked}' from {string.Join(", ", candidates)} (p={probability:F2}) in {watch.ElapsedMilliseconds} ms");
        return picked;
    }

    /// <summary>Whether the runtime that actually loaded is what the provider asked for.</summary>
    private static bool Satisfies(WhisperRuntime wanted, string? loaded) => wanted switch
    {
        WhisperRuntime.Auto => true,
        WhisperRuntime.Cpu => loaded is nameof(RuntimeLibrary.Cpu) or nameof(RuntimeLibrary.CpuNoAvx),
        WhisperRuntime.Vulkan => loaded == nameof(RuntimeLibrary.Vulkan),
        WhisperRuntime.Cuda => loaded is nameof(RuntimeLibrary.Cuda) or nameof(RuntimeLibrary.Cuda12),
        _ => false,
    };

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
            var options = new WhisperFactoryOptions { UseGpu = config.Runtime != WhisperRuntime.Cpu };
            _factory = NativeStderr.Silence(() => WhisperFactory.FromPath(ModelPath, options));
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
            .WithSpeechPadding(VadSpeechPadding)
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

            // 'auto' only uses Vulkan on real graphics hardware, never on a software renderer such as WARP or llvmpipe.
            if (runtime == WhisperRuntime.Auto && !VulkanProbe.HasHardwareGpu)
            {
                order.Remove(RuntimeLibrary.Vulkan);
                log.Info(VulkanProbe.Devices.Count == 0
                    ? "No Vulkan device; using the CPU runtime"
                    : $"Vulkan has no hardware GPU ({string.Join(", ", VulkanProbe.Devices)}); using the CPU runtime");
            }
            else if (runtime == WhisperRuntime.Vulkan && !VulkanProbe.HasHardwareGpu)
            {
                log.Warn($"\"runtime\": \"vulkan\" without a hardware GPU ({string.Join(", ", VulkanProbe.Devices)}) is slow and may give wrong text; consider \"cpu\".");
            }

            RuntimeOptions.RuntimeLibraryOrder = order;
            _configuredRuntime = runtime;
        }
    }

    public static string DownloadHint(string model) =>
        $"Download it into the models directory, e.g. https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{Path.GetFileName(model)}";

    public static string VadDownloadHint() =>
        $"Download https://huggingface.co/sandrohanea/whisper.net/resolve/v4/vad/{WhisperLocalSttConfig.DefaultVadModel} into the models directory, or set \"vad\": false.";

    /// <summary>Whisper marks non-speech as e.g. "[BLANK_AUDIO]", "[Music]" or "(silence)".</summary>
    private static bool IsNonSpeechTag(string segment) => NonSpeechTag().IsMatch(segment);

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\))\s*$")]
    private static partial Regex NonSpeechTag();
}
