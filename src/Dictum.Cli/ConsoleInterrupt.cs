namespace Dictum.Cli;

/// <summary>
/// Ctrl+C handling: a command may claim the first press (e.g. "stop recording and transcribe");
/// otherwise the press cancels the command, and a second press terminates the process.
/// </summary>
internal static class ConsoleInterrupt
{
    private static readonly Lock Gate = new();
    private static readonly CancellationTokenSource Abort = new();
    private static Action? _handler;

    public static CancellationToken Token => Abort.Token;

    public static void Install() => Console.CancelKeyPress += OnCancelKeyPress;

    /// <summary>Claims the next Ctrl+C; disposing the result releases it.</summary>
    public static IDisposable Claim(Action onInterrupt)
    {
        lock (Gate)
        {
            _handler = onInterrupt;
        }

        return new Release();
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        Action? handler;
        lock (Gate)
        {
            handler = _handler;
            _handler = null;
        }

        if (handler is not null)
        {
            e.Cancel = true;
            handler();
        }
        else if (!Abort.IsCancellationRequested)
        {
            e.Cancel = true;
            Abort.Cancel();
        }
    }

    private sealed class Release : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                _handler = null;
            }
        }
    }
}
