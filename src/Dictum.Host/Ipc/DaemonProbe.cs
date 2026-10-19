namespace Dictum.Host.Ipc;

/// <summary>What asking the daemon for its status found.</summary>
public enum DaemonPresence
{
    /// <summary>Nothing listens on the socket.</summary>
    NotRunning,

    /// <summary>The daemon answered with its status.</summary>
    Answering,

    /// <summary>Something holds the socket and took the connection, but sent no status in time.</summary>
    NotAnswering,
}

/// <param name="Status">The daemon's answer, when it is <see cref="DaemonPresence.Answering"/>.</param>
public sealed record DaemonProbe(DaemonPresence Presence, DaemonStatus? Status = null);
