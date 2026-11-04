using Dictum.Core.Text;

namespace Dictum.Core.Configuration;

public enum ConfigIssueSeverity
{
    Warning,
    Error,
}

/// <param name="Path">
/// Location in the config, e.g. <c>profiles[0].stt</c>, with the keys as the file has them, which Settings matches.
/// </param>
/// <param name="Line">1-based line in the file, when known.</param>
public sealed record ConfigIssue(ConfigIssueSeverity Severity, string Path, string Message, long? Line = null)
{
    /// <summary>
    /// The issue as every report prints it. The path and the message may quote a copied config.json, so what a terminal
    /// would act on is spelt out (<see cref="TerminalText.Show"/>); text spelt out already stays as it is.
    /// </summary>
    public override string ToString()
    {
        var where = Line is { } line ? $"line {line}: " : string.Empty;
        var path = Path.Length > 0 ? $"{TerminalText.Show(Path)}: " : string.Empty;
        return $"{where}{path}{TerminalText.Show(Message)}";
    }
}

/// <summary>The config file is missing, unreadable or invalid, or a value it refers to cannot be read. Maps to exit code 2.</summary>
public class ConfigException : Exception
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
