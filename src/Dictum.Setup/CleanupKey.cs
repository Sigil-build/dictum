using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Setup;

/// <summary>A key waiting to be written to the OS secret store under <paramref name="Name"/>.</summary>
public sealed record PendingSecret(string Name, string Value)
{
    public Task WriteAsync(ISecretStore secrets, CancellationToken ct) => secrets.SetAsync(Name, Value, ct);

    public override string ToString() => KeyRedaction.Print(nameof(PendingSecret), (this with { Value = KeyRedaction.Mask }).PrintMembers);
}

/// <summary>
/// The clean-up server's API key in setup. A key given to <c>dictum init</c> as a value never goes into config.json, which
/// may roam between machines: the config gets a <c>secret:</c> reference, and the key waits as a <see cref="PendingSecret"/>
/// until the downloads and validation succeeded, right before the config is saved.
/// </summary>
public static class CleanupKey
{
    /// <summary>
    /// After setup saved <paramref name="saved"/> over <paramref name="current"/>: deletes
    /// <see cref="SecretNames.LegacyCleanup"/> when the clean-up key of <paramref name="current"/> referred to it and
    /// nothing in <paramref name="saved"/> still does, whether a new key or reference replaced it or the key was dropped.
    /// </summary>
    public static Task DeleteLegacyAsync(ISecretStore secrets, DictumConfig current, DictumConfig saved, ILog log, CancellationToken ct) =>
        StoredKeys.DeleteLegacyCleanupAsync(
            secrets,
            (current.Processors.GetValueOrDefault(SetupPlanner.CleanupProcessorId(current)) as LlmProcessorConfig)?.ApiKey,
            saved,
            log,
            ct);

    /// <param name="current">The config init starts from, which decides the clean-up processor and so the secret's name.</param>
    /// <returns>The config's apiKey value, and the key still to write; a reference is kept as given, with nothing to write.</returns>
    public static (string Reference, PendingSecret? Pending) KeepOutOfConfig(string key, DictumConfig? current = null)
    {
        if (ConfigValueResolver.IsReference(key))
        {
            return (key, null);
        }

        var pending = new PendingSecret(SetupPlanner.CleanupSecretName(current), key);
        return (ConfigValueResolver.SecretReference(pending.Name), pending);
    }
}
