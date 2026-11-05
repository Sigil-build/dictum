using Dictum.Core.Http;
using Dictum.Plugins;

namespace Dictum.Core.Tests.Plugins;

/// <summary>
/// The client every request to a plugin goes through. The proxy setting is read from the handler: a test that set
/// <c>HTTP_PROXY</c> or <see cref="HttpClient.DefaultProxy"/> would change it for every test running beside it.
/// </summary>
public sealed class PluginHttpTests
{
    // A plugin listens on this computer only. Through HTTP_PROXY, its health polls and transcriptions, with the key in
    // their header, would go to the proxy instead, and every plugin would fail.
    [Fact]
    public void PluginRequestsNeverGoThroughAProxy()
    {
        using var handler = PluginHttp.CreateHandler();

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void TheOtherSettingsAreTheProvidersOnes()
    {
        using var plugin = PluginHttp.CreateHandler();
        using var provider = ProviderHttp.CreateHandler();
        using var client = PluginHttp.CreateClient();

        Assert.Equal(provider.PooledConnectionLifetime, plugin.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), plugin.PooledConnectionLifetime);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
