using System.Collections.Concurrent;
using Dictum.Core.Diagnostics;

namespace Dictum.Testing;

/// <summary>Keeps every line written to it, from any thread, for tests that check what was logged.</summary>
public sealed class ListLog : ILog
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

    public IEnumerable<string> Messages => Lines.Select(l => l.Message);

    public bool IncludeText => false;

    public void Write(LogLevel level, string message, Exception? exception = null) => Lines.Enqueue((level, message));

    public bool Has(string text) => Lines.Any(l => l.Message.Contains(text, StringComparison.Ordinal));
}
