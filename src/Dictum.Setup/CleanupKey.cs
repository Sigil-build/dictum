using Dictum.Core.Configuration;

namespace Dictum.Setup;

/// <summary>A key waiting to be written to the OS secret store under <paramref name="Name"/>.</summary>
public sealed record PendingSecret(string Name, string Value)
{
    public Task WriteAsync(ISecretStore secrets, CancellationToken ct) => secrets.SetAsync(Name, Value, ct);
}

/// <summary>
/// The clean-up server's API key as <c>dictum init</c> takes it. A key given as a value never goes into config.json, which
/// may roam between machines: the config gets a <c>secret:</c> reference, and the key waits as a <see cref="PendingSecret"/>
/// until the downloads and validation succeeded, right before the config is saved.
/// </summary>
public static class CleanupKey
{
    /// <returns>The config's apiKey value, and the key still to write; a reference is kept as given, with nothing to write.</returns>
    public static (string Reference, PendingSecret? Pending) KeepOutOfConfig(string key)
    {
        if (ConfigValueResolver.IsReference(key))
        {
            return (key, null);
        }

        var pending = new PendingSecret(SecretNames.LegacyCleanup, key);
        return (ConfigValueResolver.SecretReference(pending.Name), pending);
    }
}
