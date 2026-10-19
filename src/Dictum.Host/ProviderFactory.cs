using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
using Dictum.Core.Processing;
using Dictum.Core.Stt;
using Dictum.Processing;
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

    public ProviderFactory(AppPaths paths, ISecretStore secrets, ILog log)
    {
        var values = new ConfigValueResolver(secrets);
        var templates = new PromptTemplates(paths.PromptsDirectory);
        _registry = new ProviderRegistry()
            .AddStt<WhisperLocalSttConfig>((id, c) => new WhisperLocalStt(id, c, paths, log))
            .AddStt<OpenAiCompatibleSttConfig>((id, c) => new OpenAiCompatibleStt(id, c, _http, values, log))
            .AddProcessor<ReplaceProcessorConfig>((id, c) => new ReplaceProcessor(id, c, log))
            .AddProcessor<LlmProcessorConfig>((id, c) => new LlmProcessor(id, c, _http, values, templates, log));
    }

    public ISpeechToText CreateStt(string id, SttConfig config) => _registry.CreateStt(id, config);

    public ITextProcessor CreateProcessor(string id, ProcessorConfig config) => _registry.CreateProcessor(id, config);

    public void Dispose() => _http.Dispose();
}
