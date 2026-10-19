namespace Dictum.Host;

internal static class ConfiguredIds
{
    /// <summary>The ids for an "unknown id" error, or "none".</summary>
    public static string Format(IEnumerable<string> ids) => ids.Any() ? string.Join(", ", ids) : "none";
}
