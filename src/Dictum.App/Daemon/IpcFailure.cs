using System.Net.Sockets;
using System.Text.Json;
using Dictum.Host.Ipc;

namespace Dictum.App.Daemon;

/// <summary>How a request to the daemon, or to the running app, can fail without a bug on this side.</summary>
internal static class IpcFailure
{
    /// <summary>
    /// Nobody listens, the connection broke, or no answer came in time. An <see cref="OperationCanceledException"/>
    /// counts only when <paramref name="ct"/>, the caller's own token, was not cancelled: the client's timeout cancels too.
    /// </summary>
    public static bool IsTransport(Exception ex, CancellationToken ct) =>
        ex is DaemonNotRunningException or IOException or SocketException or TimeoutException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>An answer came but could not be read: not JSON, or empty.</summary>
    public static bool IsBadReply(Exception ex) => ex is JsonException or InvalidOperationException;
}
