namespace Dictum.Core.Configuration;

/// <summary>Resolves config values of the form <c>env:VAR</c> and <c>secret:name</c>; other values pass through.</summary>
public sealed class ConfigValueResolver(ISecretStore secrets)
{
    public const string EnvPrefix = "env:";
    public const string SecretPrefix = "secret:";

    public static bool IsReference(string value) =>
        value.StartsWith(EnvPrefix, StringComparison.Ordinal) || value.StartsWith(SecretPrefix, StringComparison.Ordinal);

    /// <summary>The config value that reads secret <paramref name="name"/> from the OS secret store.</summary>
    public static string SecretReference(string name) => SecretPrefix + name;

    /// <summary>The variable that an <c>env:VAR</c> value reads.</summary>
    public static bool TryGetEnvVariable(string value, out string name)
    {
        if (value.StartsWith(EnvPrefix, StringComparison.Ordinal))
        {
            name = value[EnvPrefix.Length..];
            return true;
        }

        name = "";
        return false;
    }

    public async ValueTask<string?> ResolveAsync(string? value, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (TryGetEnvVariable(value, out var name))
        {
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } resolved
                ? resolved
                : throw new ConfigException($"Environment variable '{name}' is not set.");
        }

        if (value.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            var key = value[SecretPrefix.Length..];
            return await secrets.GetAsync(key, ct).ConfigureAwait(false)
                ?? throw new ConfigException($"Secret '{key}' is not in the OS secret store.");
        }

        return value;
    }
}
