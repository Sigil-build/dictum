namespace Dictum.Core.Configuration;

/// <summary>The ids of plugins, in <c>plugins</c> and in a plugin's manifest; the config validator and the manifest check them the same way.</summary>
public static class PluginIds
{
    /// <summary>The built-in Whisper engine, which a plugin cannot take the name of.</summary>
    public const string Reserved = "whisper";

    public const int MaxLength = 40;

    public const string Rule = "must be lowercase letters, digits and '-' (at most 40), and not 'whisper'";

    /// <summary><c>^[a-z0-9][a-z0-9-]{0,39}$</c> and not <see cref="Reserved"/>.</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= MaxLength
        && id != Reserved
        && IsLowercaseOrDigit(id[0])
        && id.All(c => IsLowercaseOrDigit(c) || c == '-');

    private static bool IsLowercaseOrDigit(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
