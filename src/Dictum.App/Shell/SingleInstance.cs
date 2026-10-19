using System.Net.Sockets;
using System.Text.Json;
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

    /// <summary>Quits the running app as the tray's Quit does, for the install and uninstall scripts.</summary>
    public const string Quit = "quit";

    private static readonly TimeSpan DefaultQuitPatience = TimeSpan.FromSeconds(15);

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
    /// <paramref name="patience"/> (15 s by default: the shell waits up to 3 s for the daemon to stop). An instance that
    /// holds the socket but never answers counts as still there, never as an error.
    /// </summary>
    public static async Task<bool> QuitAsync(string socketPath, CancellationToken ct, TimeSpan? patience = null)
    {
        var wait = patience ?? DefaultQuitPatience;
        var deadline = DateTime.UtcNow + wait;
        if (await TryAsync(socketPath, Quit, ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false) == Reply.Gone)
        {
            return false;
        }

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);

            // Any command will do: the shell ignores what it does not know, and an instance that is gone answers nothing.
            if (await TryAsync(socketPath, "ping", ct, TimeSpan.FromSeconds(1)).ConfigureAwait(false) == Reply.Gone)
            {
                return true;
            }
        }

        throw new TimeoutException($"Dictum is still running after {wait.TotalSeconds:0.#} s.");
    }

    /// <summary><c>dictum-gui --quit</c> for this install: the folder of the running executable, the real processes.</summary>
    public static Task<int> QuitCommandAsync(string appSocket, string daemonSocket, TextWriter error, CancellationToken ct, TimeSpan? patience = null) =>
        QuitCommandAsync(appSocket, daemonSocket, AppContext.BaseDirectory, SystemDictumProcesses.Instance, error, ct, patience);

    /// <summary>
    /// <c>dictum-gui --quit</c> for the install scripts and the MSI. Quits a running Dictum desktop app, asks a daemon
    /// that runs from <paramref name="folder"/> to shut down too (the app's own, or one started without it: a login item,
    /// a terminal), then waits until no Dictum process from that folder runs, so an installer never meets its files in
    /// use. A daemon from another copy is left alone. 0 when nothing of this folder runs any more, 1 when something still
    /// runs after <paramref name="patience"/>. Never throws.
    /// </summary>
    public static async Task<int> QuitCommandAsync(
        string appSocket, string daemonSocket, string folder, IDictumProcesses processes, TextWriter error, CancellationToken ct, TimeSpan? patience = null)
    {
        var wait = patience ?? DefaultQuitPatience;
        var deadline = DateTime.UtcNow + wait;
        try
        {
            await QuitAsync(appSocket, ct, wait).ConfigureAwait(false);
            if (processes.RunningFrom(folder) is { Count: > 0 } running
                && await DaemonProcessAsync(daemonSocket, ct).ConfigureAwait(false) is { } daemon
                && running.Contains(daemon))
            {
                await TryAsync(daemonSocket, "shutdown", ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            while (processes.RunningFrom(folder).Count > 0)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    error.WriteLine($"Dictum is still running from {folder} after {wait.TotalSeconds:0.#} s.");
                    return 1;
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
            }

            return 0;
        }
        catch (TimeoutException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error.WriteLine($"Could not quit Dictum: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The process id the daemon on <paramref name="socket"/> reports, or null when none answers.</summary>
    private static async Task<int?> DaemonProcessAsync(string socket, CancellationToken ct)
    {
        try
        {
            var response = await new IpcClient(socket).SendAsync(new IpcRequest("status"), ct, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return response.Status?.ProcessId;
        }
        catch (DaemonNotRunningException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private enum Reply
    {
        Answered,
        Gone,
        Silent,
    }

    /// <summary>One command: answered, gone (nobody listens), or silent (no answer in time, or the connection broke).</summary>
    private static async Task<Reply> TryAsync(string socketPath, string command, CancellationToken ct, TimeSpan timeout)
    {
        try
        {
            await new IpcClient(socketPath).SendAsync(new IpcRequest(command), ct, timeout).ConfigureAwait(false);
            return Reply.Answered;
        }
        catch (DaemonNotRunningException)
        {
            return Reply.Gone;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Reply.Silent;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidOperationException)
        {
            return Reply.Silent;
        }
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
