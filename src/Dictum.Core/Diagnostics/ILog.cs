namespace Dictum.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface ILog
{
    /// <summary>
    /// True when dictated text may be written to logs (<c>--verbose</c>). Off by default for privacy.
    /// </summary>
    bool IncludeText { get; }

    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);

    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    public static void Warn(this ILog log, string message, Exception? exception = null) =>
        log.Write(LogLevel.Warning, message, exception);

    public static void Error(this ILog log, string message, Exception? exception = null) =>
        log.Write(LogLevel.Error, message, exception);

    /// <summary>Returns the text itself in verbose mode, otherwise only its length.</summary>
    public static string Redact(this ILog log, string text) =>
        log.IncludeText ? text : $"<{text.Length} chars>";
}
