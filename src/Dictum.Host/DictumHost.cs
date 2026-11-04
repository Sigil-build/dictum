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
using Dictum.Platform.Injection;
using Dictum.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Dictum.Host;

/// <summary>
/// Composition root for the CLI and its daemon. Registrations are explicit, as Native AOT needs: nothing is discovered
/// by reflection.
/// </summary>
public sealed class DictumHost : IDictationEngine
{
    private readonly ServiceProvider _services;
    private readonly ProviderFactory _providers;
    private readonly IActiveAppProvider? _activeApp;
    private readonly ConcurrentDictionary<string, ISpeechToText> _stt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ITextProcessor> _processors = new(StringComparer.Ordinal);

    /// <summary>Orders each new speech provider before or after the disposal, so that the disposal sees every one made.</summary>
    private readonly Lock _gate = new();

    private DictationPipeline? _pipeline;
    private bool _disposed;

    private DictumHost(AppPaths paths, ILog log, DictumConfig config, ISecretStore secrets, IActiveAppProvider? activeApp, Func<PluginHost>? plugins)
    {
        Paths = paths;
        Log = log;
        Config = config;
        _activeApp = activeApp;

        var services = new ServiceCollection();
        services.AddSingleton(_ => new HistoryStore(paths.HistoryFile, config.General.HistorySize));
        services.AddSingleton<IAudioSource>(_ => Environment.GetEnvironmentVariable(DictumEnvironment.AudioFile) is { Length: > 0 } file
            ? new FileAudioSource(file)
            : new PortAudioSource(log));
        _services = services.BuildServiceProvider();
        _providers = new ProviderFactory(paths, secrets, log, plugins, config.Plugins);
    }

    public AppPaths Paths { get; }

    public ILog Log { get; }

    public DictumConfig Config { get; }

    public IAudioSource Audio => _services.GetRequiredService<IAudioSource>();

    public HistoryStore History => _services.GetRequiredService<HistoryStore>();

    /// <param name="config">Null when no config file exists; ad-hoc providers (e.g. <c>--model</c>) still work.</param>
    /// <param name="secrets">Backs <c>secret:</c> values; defaults to a store that explains it is unavailable.</param>
    /// <param name="activeApp">Names the focused app when a recording stops, for a prompt's <c>{{app}}</c>; none leaves it unknown.</param>
    /// <param name="plugins">
    /// Runs the plugin providers, those made ad hoc included; it stays the caller's, and is asked for only when a plugin
    /// provider is made. Without it a plugin provider fails when it is used.
    /// </param>
    public static DictumHost Create(
        AppPaths paths,
        ILog log,
        DictumConfig? config,
        ISecretStore? secrets = null,
        IActiveAppProvider? activeApp = null,
        Func<PluginHost>? plugins = null) =>
        new(paths, log, config ?? new DictumConfig(), secrets ?? new UnavailableSecretStore(), activeApp, plugins);

    /// <summary>The state cues on the default output device, for the daemon and the Settings sound test.</summary>
    public static ISoundPlayer CreateSoundPlayer(ILog log) => new SoundPlayer(log);

    /// <exception cref="ObjectDisposedException">The host is disposed, and so would not dispose a provider made now.</exception>
    public ISpeechToText GetStt(string id) =>
        AddStt(id, key => Config.Stt.TryGetValue(key, out var config)
            ? _providers.CreateStt(key, config)
            : throw new ConfigException($"Unknown STT provider '{key}'. Configured: {ConfiguredIds.Format(Config.Stt.Keys)}."));

    public ITextProcessor GetProcessor(string id) =>
        _processors.GetOrAdd(id, key => Config.Processors.TryGetValue(key, out var config)
            ? _providers.CreateProcessor(key, config)
            : throw new ConfigException($"Unknown processor '{key}'. Configured: {ConfiguredIds.Format(Config.Processors.Keys)}."));

    /// <summary>Creates a provider that is not in the config, e.g. from <c>--model</c>.</summary>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    public ISpeechToText CreateAdHocStt(string id, SttConfig config) =>
        AddStt(id, key => _providers.CreateStt(key, config));

    public DictationProfile ResolveProfile(ProfileOverrides overrides) =>
        ProfileResolver.Resolve(Config, overrides, GetStt, GetProcessor, CreateAdHocStt);

    public DictationPipeline CreatePipeline() =>
        new(Audio, History, Log, clip => SilenceTrimmer.Trim(GainNormalizer.Normalize(clip)), _activeApp is null ? null : _activeApp.GetActiveAppAsync);

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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var stt in _stt.Values.OfType<IDisposable>())
        {
            stt.Dispose();
        }

        _providers.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The provider with this id, made on first use. A plugin provider takes its plugin's lease once used, and only its
    /// disposal lets go of it, so none is made once the host's disposal has begun.
    /// </summary>
    private ISpeechToText AddStt(string id, Func<string, ISpeechToText> create)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stt.GetOrAdd(id, create);
        }
    }
}
