using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Setup;

/// <summary>Deletes API keys from the OS secret store once the config no longer refers to them; a shared one stays.</summary>
public static class StoredKeys
{
    /// <summary>
    /// After a key replaced or removed <paramref name="previous"/>: deletes <see cref="SecretNames.LegacyCleanup"/> when
    /// <paramref name="previous"/> referred to it and nothing in <paramref name="remaining"/>, the config after the change,
    /// still does.
    /// </summary>
    public static Task DeleteLegacyCleanupAsync(ISecretStore secrets, string? previous, DictumConfig remaining, ILog log, CancellationToken ct) =>
        previous == ConfigValueResolver.SecretReference(SecretNames.LegacyCleanup)
            ? DeleteUnusedAsync(secrets, SecretNames.LegacyCleanup, remaining, log, ct)
            : Task.CompletedTask;

    /// <summary>Deletes <paramref name="secretName"/> unless something in <paramref name="remaining"/> still refers to it; a failure is only logged.</summary>
    public static async Task DeleteUnusedAsync(ISecretStore secrets, string secretName, DictumConfig remaining, ILog log, CancellationToken ct)
    {
        if (IsReferenced(remaining, ConfigValueResolver.SecretReference(secretName)))
        {
            return;
        }

        try
        {
            await secrets.DeleteAsync(secretName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"Secret '{secretName}' not deleted: {ex.Message}");
        }
    }

    // A new processor copies the first one's key reference, and a hand-edited config may share one.
    private static bool IsReferenced(DictumConfig config, string reference) =>
        config.Stt.Values.OfType<OpenAiCompatibleSttConfig>().Any(s => s.ApiKey == reference)
        || config.Processors.Values.OfType<LlmProcessorConfig>().Any(l => l.ApiKey == reference);
}
