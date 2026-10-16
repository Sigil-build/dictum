using System.Collections.Concurrent;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests;

internal sealed class ListLog : ILog
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

    public bool IncludeText => false;

    public void Write(LogLevel level, string message, Exception? exception = null) => Lines.Enqueue((level, message));

    public bool Has(string text) => Lines.Any(l => l.Message.Contains(text, StringComparison.Ordinal));
}
