using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dictum.Host.Ipc;

/// <summary>Talks to the daemon over its socket, one connection per request.</summary>
public sealed class IpcClient(string socketPath)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <param name="timeout">How long to wait for the answer; slow commands such as <c>test-stt</c> pass a longer one.</param>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var socket = await ConnectAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await ExchangeAsync(stream, reader, request, deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Opens the event stream; disposing the subscription closes it.</summary>
    public async Task<IpcSubscription> SubscribeAsync(CancellationToken ct)
    {
        var socket = await ConnectAsync(ct).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(DefaultTimeout);
            var reader = new StreamReader(stream, Encoding.UTF8);
            var response = await ExchangeAsync(stream, reader, new IpcRequest(IpcCommand.Subscribe), deadline.Token).ConfigureAwait(false);
            return new IpcSubscription(response, stream, reader);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IpcResponse> ExchangeAsync(NetworkStream stream, StreamReader reader, IpcRequest request, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest) + IpcProtocol.LineEnd;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct).ConfigureAwait(false);
        var reply = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new DaemonNotRunningException("The daemon closed the connection without answering.");
        return JsonSerializer.Deserialize(reply, IpcJsonContext.Default.IpcResponse)
            ?? throw new InvalidOperationException("The daemon sent an empty answer.");
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

/// <summary>An open <c>subscribe</c> connection: the daemon's first reply, then its events.</summary>
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
