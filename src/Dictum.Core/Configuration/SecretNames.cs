namespace Dictum.Core.Configuration;

/// <summary>The OS secret store entries that hold API keys typed into Dictum; the config refers to them as <c>secret:&lt;name&gt;</c>.</summary>
public static class SecretNames
{
    /// <summary>Where <c>dictum init</c> keeps a typed key for the clean-up server.</summary>
    public const string LegacyCleanup = "dictum-cleanup";

    public static string Stt(string providerId) => $"dictum-stt-{providerId}";

    public static string Processor(string processorId) => $"dictum-processor-{processorId}";
}
