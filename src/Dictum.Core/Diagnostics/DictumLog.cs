using System.Globalization;
using System.Text;

namespace Dictum.Core.Diagnostics;

/// <summary>
/// Writes to stderr (stdout is reserved for results) and to a size-rotated file.
/// </summary>
public sealed class DictumLog : ILog, IDisposable
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int KeptFiles = 3;

    private readonly Lock _gate = new();
    private readonly LogLevel _consoleLevel;
    private readonly TextWriter? _console;
    private readonly string? _filePath;
    private StreamWriter? _file;

    public DictumLog(LogLevel consoleLevel, bool includeText, TextWriter? console, string? logsDirectory, string fileName = AppPaths.DaemonLogName)
    {
        _consoleLevel = consoleLevel;
        IncludeText = includeText;
        _console = console;
        if (logsDirectory is not null)
        {
            _filePath = Path.Combine(logsDirectory, fileName);
        }
    }

    public bool IncludeText { get; }

    /// <summary>The log file, for messages that point at it; null without a logs directory.</summary>
    public string? FilePath => _filePath;

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        lock (_gate)
        {
            if (_console is not null && level >= _consoleLevel)
            {
                _console.WriteLine(exception is null || _consoleLevel > LogLevel.Debug
                    ? $"{Prefix(level)}{message}"
                    : $"{Prefix(level)}{message}{Environment.NewLine}{exception}");
            }

            WriteFile(level, message, exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private static string Prefix(LogLevel level) => level switch
    {
        LogLevel.Warning => "warning: ",
        LogLevel.Error => "error: ",
        _ => string.Empty,
    };

    private void WriteFile(LogLevel level, string message, Exception? exception)
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            _file ??= OpenFile(_filePath);
            if (_file.BaseStream.Length > MaxFileBytes)
            {
                _file.Dispose();
                Rotate(_filePath);
                _file = OpenFile(_filePath);
            }

            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(level.ToString().ToUpperInvariant())
                .Append(' ')
                .Append(message);
            if (exception is not null)
            {
                line.AppendLine().Append(exception);
            }

            _file.WriteLine(line.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break dictation.
        }
    }

    private static StreamWriter OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static void Rotate(string path)
    {
        for (var i = KeptFiles - 1; i >= 1; i--)
        {
            var from = $"{path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{i + 1}", overwrite: true);
            }
        }

        File.Move(path, $"{path}.1", overwrite: true);
    }
}
