using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Shell;

/// <summary>
/// One desktop app per user. The first instance listens on <c>dictum-gui.sock</c> next to the daemon's socket; the
/// IPC server's lock file is the single-instance lock. A second launch hands its command to the first and exits.
/// </summary>
public sealed class SingleInstance : IAsyncDisposable
{
    public const string ShowSettings = "show-settings";

    /// <summary>Quits the running app as the tray's Quit does (phase 4: install and uninstall scripts).</summary>
    public const string Quit = "quit";

    private readonly IpcServer _server;

    private SingleInstance(IpcServer server) => _server = server;

    public static string SocketPathFor(string daemonSocket) =>
        Path.Combine(Path.GetDirectoryName(daemonSocket)!, Path.GetFileNameWithoutExtension(daemonSocket) + "-gui.sock");

    /// <summary>Becomes the instance, or returns null when another one holds the lock.</summary>
    /// <param name="onCommand">Gets each command a later launch sends; called on a server thread.</param>
    public static async Task<SingleInstance?> AcquireAsync(string socketPath, Action<string> onCommand, ILog log, CancellationToken ct)
    {
        var server = new IpcServer(socketPath, log);
        try
        {
            await server.StartAsync(
                (request, _) =>
                {
                    onCommand(request.Command);
                    return Task.FromResult(new IpcResponse(true));
                },
                ct).ConfigureAwait(false);
            return new SingleInstance(server);
        }
        catch (DaemonAlreadyRunningException)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Hands <paramref name="command"/> to the running instance. The first instance takes its lock a moment before it
    /// listens, so a launch right after it retries for <paramref name="patience"/> (5 s by default).
    /// </summary>
    /// <exception cref="DaemonNotRunningException">Nobody answered in time.</exception>
    public static async Task NotifyAsync(string socketPath, string command, CancellationToken ct, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                await new IpcClient(socketPath).SendAsync(new IpcRequest(command), ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return;
            }
            catch (DaemonNotRunningException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Asks the running app to quit and waits until it has let go of its socket. Returns false when none answered,
    /// true when one was running and is gone; throws <see cref="TimeoutException"/> when it is still there after
    /// <paramref name="patience"/> (15 s by default: the shell waits up to 3 s for the daemon to stop).
    /// </summary>
    public static async Task<bool> QuitAsync(string socketPath, CancellationToken ct, TimeSpan? patience = null)
    {
        try
        {
            await new IpcClient(socketPath).SendAsync(new IpcRequest(Quit), ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (DaemonNotRunningException)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + (patience ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            try
            {
                // Any command will do: the shell ignores what it does not know, and an instance that is gone answers nothing.
                await new IpcClient(socketPath).SendAsync(new IpcRequest("ping"), ct, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (DaemonNotRunningException)
            {
                return true;
            }
        }

        throw new TimeoutException($"Dictum is still running after {(patience ?? TimeSpan.FromSeconds(15)).TotalSeconds:0} s.");
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
