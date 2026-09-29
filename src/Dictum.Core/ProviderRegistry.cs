using Dictum.Core.Configuration;
using Dictum.Core.Processing;
using Dictum.Core.Stt;

namespace Dictum.Core;

/// <summary>
/// Maps config record types to factories. A new provider is a config record, an implementation
/// and one <see cref="AddStt{TConfig}"/> / <see cref="AddProcessor{TConfig}"/> call in the host.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<Type, Func<string, SttConfig, IServiceProvider, ISpeechToText>> _stt = [];
    private readonly Dictionary<Type, Func<string, ProcessorConfig, IServiceProvider, ITextProcessor>> _processors = [];

    public ProviderRegistry AddStt<TConfig>(Func<string, TConfig, IServiceProvider, ISpeechToText> factory)
        where TConfig : SttConfig
    {
        _stt[typeof(TConfig)] = (id, config, services) => factory(id, (TConfig)config, services);
        return this;
    }

    public ProviderRegistry AddProcessor<TConfig>(Func<string, TConfig, IServiceProvider, ITextProcessor> factory)
        where TConfig : ProcessorConfig
    {
        _processors[typeof(TConfig)] = (id, config, services) => factory(id, (TConfig)config, services);
        return this;
    }

    public ISpeechToText CreateStt(string id, SttConfig config, IServiceProvider services) =>
        _stt.TryGetValue(config.GetType(), out var factory)
            ? factory(id, config, services)
            : throw new ConfigException($"STT provider '{id}' has type {config.GetType().Name}, which this build does not support.");

    public ITextProcessor CreateProcessor(string id, ProcessorConfig config, IServiceProvider services) =>
        _processors.TryGetValue(config.GetType(), out var factory)
            ? factory(id, config, services)
            : throw new ConfigException($"Processor '{id}' has type {config.GetType().Name}, which this build does not support.");
}
