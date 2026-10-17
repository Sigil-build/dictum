using System.Net.Sockets;
using System.Text.Json;
using Dictum.Host.Ipc;

namespace Dictum.App.Daemon;

/// <summary>What a Settings page needs from the daemon: the link and one-off requests. <see cref="DaemonSupervisor"/> in the app.</summary>
public interface IDaemonChannel
{
    DaemonLink Link { get; }

    Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null);
}

public static class DaemonChannelExtensions
{
    /// <summary>
    /// Sends when the daemon is connected. Null when it is not, or when it did not answer (it stopped, or the time ran
    /// out): the page then says that the daemon is not running. An answer that cannot be read becomes a failure with
    /// its reason, never an exception on the UI thread (final review I3). The continuation stays on the caller's thread.
    /// </summary>
    public static async Task<IpcResponse?> TrySendAsync(this IDaemonChannel channel, IpcRequest request, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (channel.Link.State != DaemonLinkState.Connected)
        {
            return null;
        }

        try
        {
            return await channel.SendAsync(request, ct, timeout);
        }
        catch (Exception ex) when (ex is DaemonNotRunningException or IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return IpcResponse.Failure(ex.Message);
        }
    }
}
