using Dictum.Core.Configuration;

namespace Dictum.Testing;

/// <summary>An in-memory secret store; never the real Credential Manager or keyring.</summary>
public sealed class FakeSecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public Exception? Fails { get; set; }

    /// <summary>Thrown by every read, as a store that cannot be reached throws.</summary>
    public Exception? ReadFails { get; set; }

    /// <summary>Holds the next store until the test completes it, as a keyring prompt would.</summary>
    public TaskCompletionSource? Held { get; set; }

    public Task<string?> GetAsync(string key, CancellationToken ct) =>
        ReadFails is { } error ? Task.FromException<string?>(error) : Task.FromResult(Values.GetValueOrDefault(key));

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        if (Held is { } held)
        {
            Held = null;
            await held.Task;
        }

        Values[key] = value;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }
}
