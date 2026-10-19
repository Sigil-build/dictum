namespace Dictum.Core.Diagnostics;

public sealed class NullLog : ILog
{
    public static NullLog Instance { get; } = new();

    public bool IncludeText => false;

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
    }
}
