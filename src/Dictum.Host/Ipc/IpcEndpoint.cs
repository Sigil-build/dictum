using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
        return Shorten(Path.Combine(paths.DataDirectory, "dictum.sock"));
    }

    /// <summary>
    /// Socket paths are limited to 108 bytes (104 on macOS). A longer path is replaced by a short one in
    /// the temp directory, derived from the original so each data directory keeps its own socket.
    /// </summary>
    public static string Shorten(string path)
    {
        const int Limit = 100;
        if (Encoding.UTF8.GetByteCount(path) <= Limit)
        {
            return path;
        }

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12];
        return Path.Combine(Path.GetTempPath(), $"dictum-{hash}.sock");
    }
}

public sealed class IpcClient(string socketPath)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <param name="timeout">How long to wait for the answer; 10 s unless the command takes longer (e.g. <c>test-stt</c>).</param>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var socket = await ConnectAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? Timeout);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var line = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest) + "\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), deadline.Token).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var reply = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false)
            ?? throw new DaemonNotRunningException("The daemon closed the connection without answering.");
        return JsonSerializer.Deserialize(reply, IpcJsonContext.Default.IpcResponse)
            ?? throw new InvalidOperationException("The daemon sent an empty answer.");
    }

    /// <summary>Opens the event stream; disposing the subscription closes it.</summary>
    public async Task<IpcSubscription> SubscribeAsync(CancellationToken ct)
    {
        var socket = await ConnectAsync(ct).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);
            var line = JsonSerializer.Serialize(new IpcRequest("subscribe"), IpcJsonContext.Default.IpcRequest) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), deadline.Token).ConfigureAwait(false);
            var reader = new StreamReader(stream, Encoding.UTF8);
            var reply = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                ?? throw new DaemonNotRunningException("The daemon closed the connection without answering.");
            var response = JsonSerializer.Deserialize(reply, IpcJsonContext.Default.IpcResponse)
                ?? throw new InvalidOperationException("The daemon sent an empty answer.");
            return new IpcSubscription(response, stream, reader);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Socket> ConnectAsync(CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
            return socket;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable
                                             or SocketError.AccessDenied || !File.Exists(socketPath))
        {
            socket.Dispose();
            throw new DaemonNotRunningException("The Dictum daemon is not running. Start it with 'dictum daemon'.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class IpcSubscription(IpcResponse response, NetworkStream stream, StreamReader reader) : IAsyncDisposable
{
    public IpcResponse Response { get; } = response;

    public async IAsyncEnumerable<IpcEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0 && JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcEvent) is { } message)
            {
                yield return message;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        reader.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
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
    private readonly IpcEventHub? _events;
    private readonly TimeSpan _writeTimeout;
    private readonly CancellationTokenSource _stop = new();
    private FileStream? _lock;
    private Socket? _listener;
    private Task? _acceptLoop;

    /// <param name="events">Where <c>subscribe</c> connections get their events; without it they get one reply, like any command.</param>
    /// <param name="writeTimeout">A subscriber that does not take its events within this time is disconnected.</param>
    public IpcServer(string socketPath, ILog log, IpcEventHub? events = null, TimeSpan? writeTimeout = null)
    {
        _socketPath = socketPath;
        _log = log;
        _events = events;
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(5);
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
            IpcSubscriber? subscriber = null;
            try
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                IpcResponse response;
                try
                {
                    var request = line is null ? null : JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest);

                    // Subscribed before the reply is built: an event published meanwhile follows the reply instead of being
                    // lost, so a client that connects while the daemon registers its hotkeys hears about it (PR 5b).
                    subscriber = request?.Command == "subscribe" ? _events?.Subscribe() : null;
                    response = request is null
                        ? IpcResponse.Failure("Empty request.")
                        : await handler(request, ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    response = IpcResponse.Failure("Malformed request.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.Error($"IPC handler failed: {ex.Message}", ex);
                    response = IpcResponse.Failure(ex.Message);
                }

                if (!response.Ok)
                {
                    subscriber?.Dispose();
                    subscriber = null;
                }

                var reply = JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse) + "\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), ct).ConfigureAwait(false);
                if (subscriber is not null)
                {
                    await StreamAsync(subscriber, stream, reader, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                _log.Debug($"IPC client dropped: {ex.Message}");
            }
            finally
            {
                subscriber?.Dispose();
            }
        }
    }

    private async Task StreamAsync(IpcSubscriber subscriber, NetworkStream stream, StreamReader reader, CancellationToken ct)
    {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = WatchForHangUpAsync(reader, closed);
        try
        {
            while (true)
            {
                var batch = await subscriber.NextAsync(closed.Token).ConfigureAwait(false);
                if (subscriber.Overflowed)
                {
                    _log.Warn("An IPC subscriber stopped reading; disconnecting it");
                    return;
                }

                var text = new StringBuilder();
                foreach (var message in batch)
                {
                    text.Append(JsonSerializer.Serialize(message, IpcJsonContext.Default.IpcEvent)).Append('\n');
                }

                using var write = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                write.CancelAfter(_writeTimeout);
                try
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), write.Token).ConfigureAwait(false);
                    subscriber.Written();
                }
                catch (OperationCanceledException) when (!closed.IsCancellationRequested)
                {
                    _log.Warn($"An IPC subscriber took over {_writeTimeout.TotalSeconds:F1} s to read; disconnecting it");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client hung up.
        }
    }

    /// <summary>A subscriber sends nothing after its request, so a read that ends means it hung up.</summary>
    private static async Task WatchForHangUpAsync(StreamReader reader, CancellationTokenSource closed)
    {
        try
        {
            while (await reader.ReadLineAsync(closed.Token).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        try
        {
            await closed.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
