using System.Collections.Concurrent;
using Dictum.Core.Diagnostics;

namespace Dictum.Testing;

/// <summary>Keeps every line written to it, from any thread, for tests that check what was logged.</summary>
public sealed class ListLog : ILog
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

    /// <summary>Each line with the exception that came with it, for tests that check what the log file would show.</summary>
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IEnumerable<string> Messages => Lines.Select(l => l.Message);

    public bool IncludeText => false;

    public void Write(LogLevel level, string message, Exception? exception = null) 
    {
        Lines.Enqueue((level, message));
        Entries.Enqueue((level, message, exception));
    }

    public bool Has(string text) => Lines.Any(l => l.Message.Contains(text, StringComparison.Ordinal));
}
