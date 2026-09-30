using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.Host.Ipc;

/// <summary>The daemon is not running (no socket, or nobody listening). Maps to exit code 4.</summary>
public sealed class DaemonNotRunningException(string message) : Exception(message);

/// <summary>Another daemon already owns the socket.</summary>
public sealed class DaemonAlreadyRunningException(string message) : Exception(message);

/// <summary>
/// Where the daemon listens: a Unix domain socket on every OS (Windows 10 1803+ has AF_UNIX).
/// <c>DICTUM_SOCKET</c> overrides the path.
/// </summary>
public static class IpcEndpoint
{
    public static string SocketPath(AppPaths paths)
    {
        if (Environment.GetEnvironmentVariable("DICTUM_SOCKET") is { Length: > 0 } custom)
        {
            return custom;
        }

        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime)
        {
            return Path.Combine(runtime, "dictum.sock");
        }

        // macOS and Linux without XDG_RUNTIME_DIR use the per-user data directory; Windows uses %LOCALAPPDATA%\Dictum.
        return Path.Combine(paths.DataDirectory, "dictum.sock");
    }
}

public sealed class IpcClient(string socketPath)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable
                                             or SocketError.AccessDenied || !File.Exists(socketPath))
        {
            throw new DaemonNotRunningException("The Dictum daemon is not running. Start it with 'dictum daemon'.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var line = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), timeout.Token).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var reply = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new DaemonNotRunningException("The daemon closed the connection without answering.");
        return JsonSerializer.Deserialize(reply, IpcJsonContext.Default.IpcResponse)
            ?? throw new InvalidOperationException("The daemon sent an empty answer.");
    }
}

/// <summary>
/// Accepts one JSON request per connection and answers with one JSON line. Holds a lock file for its
/// lifetime so only one daemon runs per user.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly ILog _log;
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _lock;
    private Socket? _listener;
    private Task? _acceptLoop;

    public IpcServer(string socketPath, ILog log)
    {
        _socketPath = socketPath;
        _log = log;
    }

    public string SocketPath => _socketPath;

    public Task StartAsync(Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
        try
        {
            _lock = new FileStream(_socketPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new DaemonAlreadyRunningException("Another Dictum daemon is already running.");
        }

        if (File.Exists(_socketPath))
        {
            // We hold the lock, so whatever is left is stale.
            File.Delete(_socketPath);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        listener.Listen(8);
        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, handler, _stop.Token);
        _log.Info($"Listening on {_socketPath}");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_listener is not null && File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        if (_lock is not null)
        {
            await _lock.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(Socket listener, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && ct.IsCancellationRequested)
            {
                return;
            }

            _ = ServeAsync(client, handler, ct);
        }
    }

    private async Task ServeAsync(Socket client, Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                IpcResponse response;
                try
                {
                    var request = line is null ? null : JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest);
                    response = request is null
                        ? IpcResponse.Failure("Empty request.")
                        : await handler(request, ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    response = IpcResponse.Failure("Malformed request.");
                }

                var reply = JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse) + "\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                _log.Debug($"IPC client dropped: {ex.Message}");
            }
        }
    }
}
