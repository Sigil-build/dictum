using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

/// <summary>The config value for a typed key, or why the key could not be stored.</summary>
public sealed record KeyResult(string? Value, string? Error);

/// <summary>
/// API keys typed into Settings (spec 7, Saving):
/// <list type="bullet">
/// <item>a plain key goes to the OS secret store, and the config gets <c>secret:&lt;name&gt;</c>, as <c>dictum init</c> does;</item>
/// <item>a value that is already a reference (<c>env:VAR</c>, <c>secret:name</c>) is kept as typed.</item>
/// </list>
/// The plain key is never returned or logged.
/// </summary>
public static class ApiKeys
{
    public const string SecretPrefix = "secret:";

    public static string SecretName(string providerId) => $"dictum-stt-{providerId}";

    public static string ProcessorSecretName(string processorId) => $"dictum-processor-{processorId}";

    public static async Task<KeyResult> StoreAsync(ISecretStore secrets, string secretName, string typed, CancellationToken ct)
    {
        var key = typed.Trim();
        if (key.Length == 0)
        {
            return new KeyResult(null, null);
        }

        if (ConfigValueResolver.IsReference(key))
        {
            return new KeyResult(key, null);
        }

        try
        {
            await secrets.SetAsync(secretName, key, ct);
            return new KeyResult(SecretPrefix + secretName, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message names the store's problem (no keyring, a dismissed prompt), never the key.
            return new KeyResult(null, ex.Message);
        }
    }

    /// <summary>Deletes the stored key when <paramref name="current"/> is this provider's own secret; a shared one stays.</summary>
    public static async Task RemoveAsync(ISecretStore secrets, string secretName, string? current, ILog log, CancellationToken ct)
    {
        if (current != SecretPrefix + secretName)
        {
            return;
        }

        try
        {
            await secrets.DeleteAsync(secretName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"Secret '{secretName}' not deleted: {ex.Message}");
        }
    }
}
