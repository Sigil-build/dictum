using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Dictum.App.Daemon;
using Dictum.Core;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Daemon;

/// <summary>"Restart daemon" replacing a daemon from another version whose answer to <c>shutdown</c> cannot be read.</summary>
public sealed class DaemonSupervisorReplaceTests : IAsyncDisposable
{
    private const int OtherProcessId = 79;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-sup", Guid.NewGuid().ToString("N")[..8]);
    private readonly ListLog _log = new();
    private readonly ConcurrentQueue<DaemonLink> _links = new();
    private readonly FakeLauncher _launcher;
    private DaemonSupervisor? _supervisor;

    public DaemonSupervisorReplaceTests()
    {
        Directory.CreateDirectory(_dir);
        _launcher = new FakeLauncher(SocketPath);
    }

    private string SocketPath => Path.Combine(_dir, "d.sock");

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }

        await _launcher.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task AnUnreadableAnswerToShutdownEndsTheOtherDaemonAndStartsOurs(string answer)
    {
        using var other = OtherVersionDaemon.Listen(SocketPath, answer);
        var supervisor = Start();
        await Eventually.WaitForAsync(() => supervisor.Link.State == DaemonLinkState.VersionMismatch, "the other version");

        supervisor.Restart();

        await Eventually.WaitForAsync(() => supervisor.Link.State is DaemonLinkState.Connected or DaemonLinkState.Failed, "the replacement");
        Assert.DoesNotContain(_links, l => l.State == DaemonLinkState.Failed);
        Assert.Equal([OtherProcessId], _launcher.Ended);
        Assert.Equal(1, _launcher.Launches);
        Assert.Equal(DaemonLinkState.Connected, supervisor.Link.State);
    }

    private DaemonSupervisor Start()
    {
        var options = new SupervisorOptions
        {
            RetryDelay = TimeSpan.FromMilliseconds(20),
            StartTimeout = TimeSpan.FromSeconds(5),
            QuitTimeout = TimeSpan.FromSeconds(1),
        };
        _supervisor = new DaemonSupervisor(SocketPath, () => true, _launcher, _log, AppPaths.GuiLogName, options: options);
        _supervisor.LinkChanged += _links.Enqueue;
        _supervisor.Start();
        return _supervisor;
    }

    /// <summary>
    /// A daemon of the next protocol version on a raw socket: it reports its status, and answers <c>shutdown</c> with a
    /// given line after it stops listening.
    /// </summary>
    private sealed class OtherVersionDaemon : IDisposable
    {
        private static readonly string StatusLine =
            $$$"""{"ok":true,"status":{"state":"Idle","profiles":[],"hotkeyBackend":"other","injectorBackend":"other","processId":{{{OtherProcessId}}},"protocol":{{{IpcProtocol.Version + 1}}}}}""";

        private readonly Socket _listener;
        private readonly string _shutdownAnswer;

        private OtherVersionDaemon(Socket listener, string shutdownAnswer)
        {
            _listener = listener;
            _shutdownAnswer = shutdownAnswer;
        }

        public static OtherVersionDaemon Listen(string path, string shutdownAnswer)
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen();
            var daemon = new OtherVersionDaemon(listener, shutdownAnswer);
            _ = Task.Run(daemon.ServeAsync);
            return daemon;
        }

        public void Dispose() => _listener.Dispose();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptAsync();
                    await using var stream = new NetworkStream(client);
                    using var reader = new StreamReader(stream);
                    var request = await reader.ReadLineAsync() ?? "";
                    var shutdown = request.Contains($"\"{IpcCommand.Shutdown}\"", StringComparison.Ordinal);
                    if (shutdown)
                    {
                        // Gone before the answer arrives, so the supervisor's next connection finds no daemon.
                        _listener.Dispose();
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes((shutdown ? _shutdownAnswer : StatusLine) + "\n"));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                // It stopped listening, or the test is over and closed the listener.
            }
        }
    }
}
