using Dictum.Host.Ipc;

namespace Dictum.App.Daemon;

public enum DaemonLinkState
{
    /// <summary>No config file yet; the daemon starts once there is one.</summary>
    NotConfigured,

    /// <summary>Connecting, or waiting for a daemon that was just started.</summary>
    Starting,

    Connected,

    /// <summary>The daemon went away unexpectedly and is being started again.</summary>
    Restarting,

    /// <summary>The daemon could not be kept running; <see cref="DaemonLink.Problem"/> says why. "Restart daemon" tries again.</summary>
    Failed,

    /// <summary>The daemon that answers speaks another protocol (an older or newer Dictum).</summary>
    VersionMismatch,

    /// <summary>Quit Dictum asked the daemon to stop.</summary>
    Stopped,
}

/// <param name="Status">The daemon's last status while connected; also the old daemon's status on a version mismatch, when it sent one.</param>
/// <param name="Problem">Why the link is down, in words for the status window.</param>
public sealed record DaemonLink(DaemonLinkState State, DaemonStatus? Status = null, string? Problem = null);
