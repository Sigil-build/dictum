namespace Dictum.Core.Configuration;

/// <summary>OS-backed storage for API keys (Credential Manager, Secret Service).</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string value, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>Stands in where the OS has no supported secret store: every call fails and points at <c>env:VAR</c>.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    private const string Message = "The OS secret store is not supported yet; use 'env:VAR' for API keys.";

    public Task<string?> GetAsync(string key, CancellationToken ct) => throw new ConfigException(Message);

    public Task SetAsync(string key, string value, CancellationToken ct) => throw new ConfigException(Message);

    public Task DeleteAsync(string key, CancellationToken ct) => throw new ConfigException(Message);
}
