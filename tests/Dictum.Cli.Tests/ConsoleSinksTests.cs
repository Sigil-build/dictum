using System.Net;
using System.Net.Sockets;
using System.Text;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Testing;

namespace Dictum.Cli.Tests;

/// <summary>What an exception from a remote server, or the log, puts on the console: spelt out, never raw.</summary>
public sealed class ConsoleSinksTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _configFile;

    public ConsoleSinksTests(CliSandbox sandbox) => _configFile = sandbox.File($"sinks-{Guid.NewGuid():N}.json");

    public void Dispose() => _dir.Dispose();

    /// <summary>A server whose answer holds an escape sequence in a header name: the exception it causes carries those bytes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AServersMalformedAnswerReachesTheConsoleSpeltOut(bool verbose)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    _ = await stream.ReadAsync(buffer, cts.Token);
                    await stream.WriteAsync(Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nX\u001b]0;PWNED\u0007: v\r\nContent-Length: 0\r\n\r\n"), cts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }
        }, TestContext.Current.CancellationToken);

        var config = new DictumConfig();
        config.Stt["remote"] = new OpenAiCompatibleSttConfig { BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "m" };
        config.Profiles.Add(new ProfileConfig { Id = "remote", Stt = "remote" });
        new ConfigStore(_configFile, NullLog.Instance).Save(config);
        var wav = _dir.File("sample.wav");
        using (var stream = File.Create(wav))
        {
            Audio.Wav.WritePcm16(stream, new float[16_000], 16_000);
        }

        string[] args = ["stt", "test", "remote", "--file", wav, "--config", _configFile];
        using var console = new ConsoleCapture();
        var code = await console.RunAsync(verbose ? [.. args, "--verbose"] : args);
        await cts.CancelAsync();
        listener.Stop();
        await Task.WhenAny(serving, Task.Delay(1000, TestContext.Current.CancellationToken));

        var all = console.Out.ToString() + console.Error;
        Assert.NotEqual(0, code);
        Assert.False(all.Contains('\u001b', StringComparison.Ordinal), all);
        Assert.False(all.Contains('\u0007', StringComparison.Ordinal), all);
        Assert.Contains("\\u001b", all, StringComparison.Ordinal);
    }
}
