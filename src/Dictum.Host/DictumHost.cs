using System.Collections.Concurrent;
using Dictum.Audio;
using Dictum.Audio.PortAudio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Core.Pipeline;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Host.Daemon;
using Dictum.Processing;
using Dictum.Stt.OpenAi;
using Dictum.Stt.Whisper;
using Microsoft.Extensions.DependencyInjection;

namespace Dictum.Host;

/// <summary>
/// Composition root for the CLI and its daemon. Registrations are explicit, as Native AOT needs: nothing is discovered
/// by reflection.
/// </summary>
public sealed class DictumHost : IDictationEngine
{
    private readonly ServiceProvider _services;
    private readonly ProviderRegistry _registry;
    private readonly ConcurrentDictionary<string, ISpeechToText> _stt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ITextProcessor> _processors = new(StringComparer.Ordinal);
    private DictationPipeline? _pipeline;

    private DictumHost(AppPaths paths, ILog log, DictumConfig config, ISecretStore secrets)
    {
        Paths = paths;
        Log = log;
        Config = config;

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(log);
        services.AddSingleton(config);
        services.AddSingleton(secrets);
        services.AddSingleton(sp => new ConfigValueResolver(sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton(_ => CreateHttpClient());
        services.AddSingleton(_ => new PromptTemplates(paths.PromptsDirectory));
        services.AddSingleton(_ => new HistoryStore(paths.HistoryFile, config.General.HistorySize));
        services.AddSingleton<IAudioSource>(_ => Environment.GetEnvironmentVariable("DICTUM_AUDIO_FILE") is { Length: > 0 } file
            ? new FileAudioSource(file)
            : new PortAudioSource(log));
        _services = services.BuildServiceProvider();

        _registry = new ProviderRegistry()
            .AddStt<WhisperLocalSttConfig>((id, c, sp) =>
                new WhisperLocalStt(id, c, sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILog>()))
            .AddStt<OpenAiCompatibleSttConfig>((id, c, sp) =>
                new OpenAiCompatibleStt(id, c, sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ConfigValueResolver>(), sp.GetRequiredService<ILog>()))
            .AddProcessor<ReplaceProcessorConfig>((id, c, sp) =>
                new ReplaceProcessor(id, c, sp.GetRequiredService<ILog>()))
            .AddProcessor<LlmProcessorConfig>((id, c, sp) =>
                new LlmProcessor(id, c, sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ConfigValueResolver>(), sp.GetRequiredService<PromptTemplates>(), sp.GetRequiredService<ILog>()));
    }

    public AppPaths Paths { get; }

    public ILog Log { get; }

    public DictumConfig Config { get; }

    public IAudioSource Audio => _services.GetRequiredService<IAudioSource>();

    public HistoryStore History => _services.GetRequiredService<HistoryStore>();

    /// <param name="config">Null when no config file exists; ad-hoc providers (e.g. <c>--model</c>) still work.</param>
    /// <param name="secrets">Backs <c>secret:</c> values; defaults to a store that explains it is unavailable.</param>
    public static DictumHost Create(AppPaths paths, ILog log, DictumConfig? config, ISecretStore? secrets = null) =>
        new(paths, log, config ?? new DictumConfig(), secrets ?? new UnavailableSecretStore());

    public ISpeechToText GetStt(string id) =>
        _stt.GetOrAdd(id, key => Config.Stt.TryGetValue(key, out var config)
            ? _registry.CreateStt(key, config, _services)
            : throw new ConfigException($"Unknown STT provider '{key}'. Configured: {Known(Config.Stt.Keys)}."));

    public ITextProcessor GetProcessor(string id) =>
        _processors.GetOrAdd(id, key => Config.Processors.TryGetValue(key, out var config)
            ? _registry.CreateProcessor(key, config, _services)
            : throw new ConfigException($"Unknown processor '{key}'. Configured: {Known(Config.Processors.Keys)}."));

    /// <summary>Creates a provider that is not in the config, e.g. from <c>--model</c>.</summary>
    public ISpeechToText CreateAdHocStt(string id, SttConfig config) =>
        _stt.GetOrAdd(id, key => _registry.CreateStt(key, config, _services));

    public DictationProfile ResolveProfile(ProfileOverrides overrides) =>
        ProfileResolver.Resolve(Config, overrides, GetStt, GetProcessor, CreateAdHocStt);

    public DictationPipeline CreatePipeline() =>
        new(Audio, History, Log, clip => SilenceTrimmer.Trim(GainNormalizer.Normalize(clip)));

    /// <summary>The long-lived pipeline used by the daemon.</summary>
    public DictationPipeline Pipeline => _pipeline ??= CreatePipeline();

    public DictationProfile ResolveProfile(string profileId) => ResolveProfile(new ProfileOverrides(Profile: profileId));

    public async Task WarmUpAsync(CancellationToken ct)
    {
        foreach (var id in Config.Profiles.Select(p => p.Stt).Distinct())
        {
            var status = await GetStt(id).CheckAsync(ct).ConfigureAwait(false);
            Log.Info($"Warm-up {id}: {status.Level} {status.Detail}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stt in _stt.Values.OfType<IDisposable>())
        {
            stt.Dispose();
        }

        await _services.DisposeAsync().ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            // Each provider applies its own timeout from the config.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static string Known(IEnumerable<string> keys) =>
        keys.Any() ? string.Join(", ", keys) : "none";
}
