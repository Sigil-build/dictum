using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Host.Tests;

/// <summary>What the factory needs to make a plugin provider, and when it asks for it.</summary>
public sealed class ProviderFactoryTests : IDisposable
{
    private static readonly PluginSttConfig Plugin = new() { Plugin = "test", Model = "m1" };

    private readonly TempDirectory _dir = new();
    private readonly AppPaths _paths;

    public ProviderFactoryTests()
    {
        _paths = new AppPaths(_dir.Path, _dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    // Ledger 88: a process without plugin processes of its own, such as the desktop app, says which ones run plugins.
    [Fact]
    public void APluginProviderWithoutAHostFailsWithConfigError()
    {
        using var providers = new ProviderFactory(_paths, new FakeSecrets(), NullLog.Instance);

        var error = Assert.Throws<ConfigException>(() => providers.CreateStt("p", Plugin));

        Assert.Equal("STT provider 'p' runs a plugin, which only the daemon and the CLI start.", error.Message);
    }

    // Only a plugin provider asks for the plugin host, so that a command that uses none makes no plugin client.
    [Fact]
    public async Task OnlyAPluginProviderAsksForThePluginHost()
    {
        var asked = 0;
        await using var plugins = new PluginHost(new PluginTrustStore(_dir.File("plugin-trust.json"), NullLog.Instance), NullProcessGroups.Instance, NullLog.Instance);
        using var providers = new ProviderFactory(_paths, new FakeSecrets(), NullLog.Instance, () =>
        {
            asked++;
            return plugins;
        });

        providers.CreateStt("w", new WhisperLocalSttConfig { Model = "m.bin" });
        Assert.Equal(0, asked);

        Assert.IsType<PluginStt>(providers.CreateStt("p", Plugin));
        Assert.Equal(1, asked);
    }
}
