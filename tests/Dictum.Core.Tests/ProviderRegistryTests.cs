using Dictum.Core.Configuration;

namespace Dictum.Core.Tests;

public class ProviderRegistryTests
{
    [Fact]
    public void CreatesProvidersByConfigType()
    {
        var registry = new ProviderRegistry()
            .AddStt<WhisperLocalSttConfig>((id, config, _) => new FakeStt(config.Model))
            .AddProcessor<ReplaceProcessorConfig>((id, _, _) => new FakeProcessor(id, t => t));

        var stt = registry.CreateStt("local", new WhisperLocalSttConfig { Model = "m.bin" }, EmptyServices.Instance);
        var processor = registry.CreateProcessor("terms", new ReplaceProcessorConfig(), EmptyServices.Instance);

        Assert.Equal("fake-stt", stt.Id);
        Assert.Equal("terms", processor.Id);
    }

    [Fact]
    public void UnregisteredTypeIsAConfigError()
    {
        var registry = new ProviderRegistry();

        Assert.Throws<ConfigException>(() =>
            registry.CreateStt("remote", new OpenAiCompatibleSttConfig { BaseUrl = "http://x/v1", Model = "m" }, EmptyServices.Instance));
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
