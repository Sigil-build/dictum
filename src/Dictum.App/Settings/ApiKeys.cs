using System.Globalization;
using Dictum.App.Resources;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

/// <summary>The config value for a typed key, or why the key could not be stored.</summary>
public sealed record KeyResult(string? Value, string? Error);

/// <summary>
/// API keys typed into Settings:
/// <list type="bullet">
/// <item>a plain key goes to the OS secret store, and the config gets <c>secret:&lt;name&gt;</c>, as <c>dictum init</c> does;</item>
/// <item>a value that is already a reference (<c>env:VAR</c>, <c>secret:name</c>) is kept as typed.</item>
/// </list>
/// The plain key is never returned or logged.
/// </summary>
public static class ApiKeys
{
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
            return new KeyResult(ConfigValueResolver.SecretReference(secretName), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message names the store's problem (no keyring, a dismissed prompt), never the key.
            return new KeyResult(null, ex.Message);
        }
    }

    /// <summary>What a key field says about the configured key, which it never shows.</summary>
    public static string Describe(string? apiKey) => apiKey switch
    {
        null => Strings.SettingsProvidersNoKey,
        var key when key.StartsWith(ConfigValueResolver.SecretPrefix, StringComparison.Ordinal) => Strings.SettingsProvidersKeyStored,
        var key when ConfigValueResolver.TryGetEnvVariable(key, out var variable) =>
            string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersKeyEnv, variable),
        _ => Strings.SettingsProvidersKeyPlain,
    };

    /// <summary>
    /// Deletes the stored key when <paramref name="current"/> is this provider's own secret, or
    /// <see cref="SecretNames.LegacyCleanup"/>, and nothing in <paramref name="remaining"/>, the config after the change,
    /// still refers to it; a shared one stays.
    /// </summary>
    public static Task RemoveAsync(ISecretStore secrets, string secretName, string? current, DictumConfig remaining, ILog log, CancellationToken ct) =>
        current == ConfigValueResolver.SecretReference(secretName)
            ? DeleteUnusedAsync(secrets, secretName, remaining, log, ct)
            : DeleteLegacyCleanupAsync(secrets, current, remaining, log, ct);

    /// <summary>
    /// After a typed key replaced <paramref name="previous"/>: deletes <see cref="SecretNames.LegacyCleanup"/> when
    /// <paramref name="previous"/> referred to it and nothing in <paramref name="remaining"/> still does.
    /// </summary>
    public static Task DeleteLegacyCleanupAsync(ISecretStore secrets, string? previous, DictumConfig remaining, ILog log, CancellationToken ct) =>
        previous == ConfigValueResolver.SecretReference(SecretNames.LegacyCleanup)
            ? DeleteUnusedAsync(secrets, SecretNames.LegacyCleanup, remaining, log, ct)
            : Task.CompletedTask;

    private static async Task DeleteUnusedAsync(ISecretStore secrets, string secretName, DictumConfig remaining, ILog log, CancellationToken ct)
    {
        if (IsReferenced(remaining, ConfigValueResolver.SecretReference(secretName)))
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

    // A new processor copies the first one's key reference, and a hand-edited config may share one.
    private static bool IsReferenced(DictumConfig config, string reference) =>
        config.Stt.Values.OfType<OpenAiCompatibleSttConfig>().Any(s => s.ApiKey == reference)
        || config.Processors.Values.OfType<LlmProcessorConfig>().Any(l => l.ApiKey == reference);
}
