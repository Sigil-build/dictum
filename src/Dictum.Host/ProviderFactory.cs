using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Plugins;
using Dictum.Processing;
using Dictum.Setup;
using Dictum.Stt.OpenAi;
using Dictum.Stt.Whisper;

namespace Dictum.Host;

/// <summary>
/// Creates STT providers and text processors from their config records. Everything it creates shares one HttpClient,
/// one <c>env:</c>/<c>secret:</c> resolver and one set of prompt templates, so it must outlive them.
/// </summary>
public sealed class ProviderFactory : IDisposable
{
    private readonly HttpClient _http = ProviderHttp.CreateClient();
    private readonly ProviderRegistry _registry;

    /// <param name="plugins">
    /// The plugin processes of this Dictum process, which only the daemon and the CLI have. Asked for when a plugin
    /// provider is created, and only then, so that a process that uses none makes none. Without it, creating a plugin
    /// provider throws; every other provider is created as usual.
    /// </param>
    /// <param name="registrations">The user's plugins by id, the <c>plugins</c> section of the config.</param>
    public ProviderFactory(
        AppPaths paths,
        ISecretStore secrets,
        ILog log,
        Func<PluginHost>? plugins = null,
        IReadOnlyDictionary<string, PluginRegistration>? registrations = null)
    {
        var values = new ConfigValueResolver(secrets);
        var templates = new PromptTemplates(paths.PromptsDirectory);
        var resolver = new PluginResolver(paths, registrations ?? new Dictionary<string, PluginRegistration>());
        _registry = new ProviderRegistry()
            .AddStt<WhisperLocalSttConfig>((id, c) => new WhisperLocalStt(id, c, paths, log, CatalogSource))
            .AddStt<OpenAiCompatibleSttConfig>((id, c) => new OpenAiCompatibleStt(id, c, _http, values, log))
            .AddStt<PluginSttConfig>((id, c) => plugins is null
                ? throw new ConfigException($"STT provider '{id}' runs a plugin, which only the daemon and the CLI start.")
                : new PluginStt(id, c, resolver, plugins(), values, log))
            .AddProcessor<ReplaceProcessorConfig>((id, c) => new ReplaceProcessor(id, c, log))
            .AddProcessor<LlmProcessorConfig>((id, c) => new LlmProcessor(id, c, _http, values, templates, log));
    }

    public ISpeechToText CreateStt(string id, SttConfig config) => _registry.CreateStt(id, config);

    public ITextProcessor CreateProcessor(string id, ProcessorConfig config) => _registry.CreateProcessor(id, config);

    public void Dispose() => _http.Dispose();

    /// <summary>The catalog's id and URL for a model file, so that a missing model's hint names the pull that fetches it.</summary>
    private static ModelSource? CatalogSource(string file) =>
        Catalog.BuiltIn.ForFile(file) is { } entry ? new ModelSource(entry.Id, entry.Url) : null;
}
