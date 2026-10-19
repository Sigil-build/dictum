namespace Dictum.App.Shell;

/// <summary>What a later launch can ask of the running app before it has started.</summary>
[Flags]
public enum PendingCommand
{
    None = 0,
    ShowSettings = 1,
    Quit = 2,
}

/// <summary>
/// Whether the shell has started, and what a later launch asked before it had. A command and the start meet here from
/// two threads (the single-instance server's and the UI thread). Both mark one word in a single atomic step, so exactly
/// one of them sees the other's mark: a command is carried out once, by the start when it came first and by its sender
/// otherwise.
/// </summary>
public sealed class StartGate
{
    private const int Started = 1 << 30;

    private int _state;

    /// <summary>True when the shell has started and the caller carries <paramref name="command"/> out now; false when <see cref="Open"/> returns it.</summary>
    public bool Request(PendingCommand command) => (Interlocked.Or(ref _state, (int)command) & Started) != 0;

    /// <summary>Marks the shell started; returns what was asked before.</summary>
    public PendingCommand Open() => (PendingCommand)(Interlocked.Or(ref _state, Started) & ~Started);
}
