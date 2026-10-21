namespace Dictum.Core.Configuration;

/// <summary>OS-backed storage for API keys (Credential Manager, Secret Service).</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);

    Task SetAsync(string key, string value, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// There is no secret store to keep keys in: no Secret Service runs, or the OS has none Dictum supports. A key then
/// has to come from <c>env:VAR</c>, so hints name that instead of storing the key.
/// </summary>
public sealed class SecretStoreUnavailableException(string message) : ConfigException(message);

/// <summary>Stands in where the OS has no supported secret store: every call fails and points at <c>env:VAR</c>.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    private const string Message = "The OS secret store is not supported yet; use 'env:VAR' for API keys.";

    public Task<string?> GetAsync(string key, CancellationToken ct) => throw new SecretStoreUnavailableException(Message);

    public Task SetAsync(string key, string value, CancellationToken ct) => throw new SecretStoreUnavailableException(Message);

    public Task DeleteAsync(string key, CancellationToken ct) => throw new SecretStoreUnavailableException(Message);
}
