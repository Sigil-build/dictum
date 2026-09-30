using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.Integration.Tests;

public class IpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-ipc", Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public IpcTests() => Directory.CreateDirectory(_dir);

    private string SocketPath => Path.Combine(_dir, "d.sock");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task RequestReachesTheHandlerAndTheAnswerComesBack()
    {
        await using var server = new IpcServer(SocketPath, NullLog.Instance);
        await server.StartAsync((request, _) => Task.FromResult(IpcResponse.Success(Status($"{request.Command}:{request.Profile}"))), _ct);

        var response = await new IpcClient(SocketPath).SendAsync(new IpcRequest("toggle", "dictate-uk"), _ct);

        Assert.True(response.Ok);
        Assert.Equal("toggle:dictate-uk", response.Status!.State);
    }

    [Fact]
    public async Task HandlesSeveralClientsInARow()
    {
        await using var server = new IpcServer(SocketPath, NullLog.Instance);
        var count = 0;
        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status($"{Interlocked.Increment(ref count)}"))), _ct);
        var client = new IpcClient(SocketPath);

        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal($"{i}", (await client.SendAsync(new IpcRequest("status"), _ct)).Status!.State);
        }
    }

    [Fact]
    public async Task SecondServerIsRejected()
    {
        await using var first = new IpcServer(SocketPath, NullLog.Instance);
        await first.StartAsync((_, _) => Task.FromResult(IpcResponse.Failure("x")), _ct);
        await using var second = new IpcServer(SocketPath, NullLog.Instance);

        await Assert.ThrowsAsync<DaemonAlreadyRunningException>(() =>
            second.StartAsync((_, _) => Task.FromResult(IpcResponse.Failure("x")), _ct));
    }

    [Fact]
    public async Task ClientWithoutServerReportsDaemonNotRunning() =>
        await Assert.ThrowsAsync<DaemonNotRunningException>(() => new IpcClient(SocketPath).SendAsync(new IpcRequest("status"), _ct));

    [Fact]
    public async Task StaleSocketFileIsReplaced()
    {
        await File.WriteAllTextAsync(SocketPath, "stale", _ct);
        await using var server = new IpcServer(SocketPath, NullLog.Instance);

        await server.StartAsync((_, _) => Task.FromResult(IpcResponse.Success(Status("fresh"))), _ct);

        Assert.Equal("fresh", (await new IpcClient(SocketPath).SendAsync(new IpcRequest("status"), _ct)).Status!.State);
    }

    private static DaemonStatus Status(string state) => new(state, null, [], "test", "test", Environment.ProcessId);
}
