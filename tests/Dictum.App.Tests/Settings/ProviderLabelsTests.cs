using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Settings;

public sealed class ProviderLabelsTests
{
    [Fact]
    public void APluginProviderIsLabelledWithItsPlugin() =>
        Assert.Equal("Plugin test", ProviderLabels.For(new PluginSttConfig { Plugin = "test", Model = "m1" }));
}
