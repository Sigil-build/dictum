namespace Dictum.Core.Http;

/// <summary>The HTTP client settings for the servers that STT providers and processors talk to.</summary>
public static class ProviderHttp
{
    /// <summary>Pooled connections are renewed this often, so a provider whose address changes is found again.</summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    public static HttpClient CreateClient() => CreateClient(CreateHandler());

    /// <summary>The handler of <see cref="CreateClient()"/>, for a client that changes one of its settings.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { PooledConnectionLifetime = ConnectionLifetime };

    /// <summary>A client over <paramref name="handler"/>, which it disposes with itself.</summary>
    public static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler)
        {
            // Each provider applies its own timeout from the config.
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
