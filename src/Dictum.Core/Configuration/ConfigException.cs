namespace Dictum.Core.Configuration;

public enum ConfigIssueSeverity
{
    Warning,
    Error,
}

/// <param name="Path">Location in the config, e.g. <c>profiles[0].stt</c>.</param>
/// <param name="Line">1-based line in the file, when known.</param>
public sealed record ConfigIssue(ConfigIssueSeverity Severity, string Path, string Message, long? Line = null)
{
    public override string ToString()
    {
        var where = Line is { } line ? $"line {line}: " : string.Empty;
        var path = Path.Length > 0 ? $"{Path}: " : string.Empty;
        return $"{where}{path}{Message}";
    }
}

/// <summary>The config file is missing, unreadable or invalid. Maps to exit code 2.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : this(message, [])
    {
    }

    public ConfigException(string message, IReadOnlyList<ConfigIssue> issues, Exception? inner = null)
        : base(message, inner)
    {
        Issues = issues;
    }

    public IReadOnlyList<ConfigIssue> Issues { get; }
}
