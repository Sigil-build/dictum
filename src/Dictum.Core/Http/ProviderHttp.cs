namespace Dictum.Core.Http;

/// <summary>The HTTP client settings for the servers that STT providers and processors talk to.</summary>
public static class ProviderHttp
{
    /// <summary>Pooled connections are renewed this often, so a provider whose address changes is found again.</summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime })
        {
            // Each provider applies its own timeout from the config.
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
