using Dictum.Core.Http;

namespace Dictum.Plugins;

/// <summary>
/// The HTTP client for plugins: the providers' settings, but never a proxy. A plugin listens only on this computer, so
/// with <c>HTTP_PROXY</c> set and no <c>NO_PROXY</c>, its requests, with the key in their header, would go to the proxy.
/// </summary>
public static class PluginHttp
{
    public static HttpClient CreateClient() => ProviderHttp.CreateClient(CreateHandler());

    public static SocketsHttpHandler CreateHandler()
    {
        var handler = ProviderHttp.CreateHandler();
        handler.UseProxy = false;
        return handler;
    }
}
