using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dictum.Core.Diagnostics;

namespace Dictum.Host.Ipc;

/// <summary>
/// Accepts one JSON request per connection and answers with one JSON line. Holds a lock file for its
/// lifetime so only one daemon runs per user.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private const string LockFileSuffix = ".lock";

    /// <summary>The accept loop hands every connection off at once, so a short queue of pending ones is plenty.</summary>
    private const int ListenBacklog = 8;

    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(5);

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
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
    }

    public string SocketPath => _socketPath;

    public void Start(Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
        try
        {
            _lock = new FileStream(_socketPath + LockFileSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new DaemonAlreadyRunningException("Another Dictum daemon is already running.");
        }

        Socket? listener = null;
        try
        {
            if (File.Exists(_socketPath))
            {
                // We hold the lock, so whatever is left is stale.
                File.Delete(_socketPath);
            }

            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            listener.Listen(ListenBacklog);
        }
        catch
        {
            // A server that cannot listen holds nothing: neither the socket nor the lock that would turn away the next one.
            listener?.Dispose();
            _lock.Dispose();
            _lock = null;
            throw;
        }

        _listener = listener;
        _acceptLoop = AcceptLoopAsync(listener, handler, _stop.Token);
        _log.Info($"Listening on {_socketPath}");
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
                // The loop ends by cancellation.
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
                    // lost, so a client that connects while the daemon registers its hotkeys hears about it.
                    subscriber = request?.Command == IpcCommand.Subscribe ? _events?.Subscribe() : null;
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

                var reply = JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse) + IpcProtocol.LineEnd;
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
                    text.Append(JsonSerializer.Serialize(message, IpcJsonContext.Default.IpcEvent)).Append(IpcProtocol.LineEnd);
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
                // Whatever a subscriber sends is ignored.
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A failed or cancelled read ends the watch like the end of the stream.
        }

        try
        {
            await closed.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The event stream already ended and disposed the token source.
        }
    }
}
