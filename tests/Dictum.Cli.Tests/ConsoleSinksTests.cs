using Dictum.Core;
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

    /// <summary>What a daemon reports, and the names a config gives, reach the terminal spelt out; --json stays JSON.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("start")]
    [InlineData("toggle")]
    public async Task ADaemonsAnswerIsPrintedSpeltOut(string command)
    {
        await using var server = new Dictum.Host.Ipc.IpcServer(Dictum.Host.Ipc.IpcEndpoint.SocketPath(AppPaths.FromEnvironment()), NullLog.Instance);
        server.Start((_, _) => Task.FromResult(command == "start"
            ? Dictum.Host.Ipc.IpcResponse.Failure("no \u001b[2Jprovider")
            : Dictum.Host.Ipc.IpcResponse.Success(new Dictum.Host.Ipc.DaemonStatus(
                "Id\u001b[2Jle", "act\u001b[2Jive", [new Dictum.Host.Ipc.ProfileStatus("p\u001b[2Jid", "Ctrl+A", "tog\u001b[2Jgle", true)], "hk", "inj", 1, Warning: "w\u001b[2Jarn"))));

        using var console = new ConsoleCapture();
        _ = await console.RunAsync([command, "--config", _configFile]);

        var all = console.Out.ToString() + console.Error;
        Assert.False(all.Contains('\u001b', StringComparison.Ordinal), all);
        Assert.Contains("\\u001b[2J", all, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    public async Task AProfileNameFromTheConfigIsPrintedSpeltOut(string command)
    {
        var config = new DictumConfig();
        config.Stt["s\u001b[2J"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:1/v1", Model = "m" };
        config.Profiles.Add(new ProfileConfig { Id = "p\u001b[2Jid", Name = "n\u001b[2Jame", Stt = "s\u001b[2J" });
        new ConfigStore(_configFile, NullLog.Instance).Save(config);

        using var console = new ConsoleCapture();
        _ = await console.RunAsync(command == "list" ? ["profiles", "list", "--config", _configFile] : ["profiles", "show", "p\u001b[2Jid", "--config", _configFile]);

        var all = console.Out.ToString() + console.Error;
        Assert.False(all.Contains('\u001b', StringComparison.Ordinal), all);
        Assert.Contains("p\\u001b[2Jid", all, StringComparison.Ordinal);
    }

    /// <summary>A health detail can carry what a config or a server said, whichever provider made it; every command that prints one spells it out.</summary>
    [Theory]
    [InlineData("stt-test", false)]
    [InlineData("doctor", false)]
    [InlineData("doctor", true)]
    public async Task AHealthDetailWithHiddenCharactersIsPrintedSpeltOut(string command, bool json)
    {
        var config = new DictumConfig();
        config.Stt["local"] = new WhisperLocalSttConfig { Model = "m\u001b[2Jodel.bin" };
        config.Profiles.Add(new ProfileConfig { Id = "local", Stt = "local" });
        new ConfigStore(_configFile, NullLog.Instance).Save(config);

        string[] args = command == "doctor" ? (json ? ["doctor", "--json"] : ["doctor"]) : ["stt", "test", "local"];
        using var console = new ConsoleCapture();
        _ = await console.RunAsync([.. args, "--config", _configFile]);

        var all = console.Out.ToString() + console.Error;
        Assert.False(all.Contains('\u001b', StringComparison.Ordinal), all);
        // --json is already escaped by the JSON writer (as \u001B), and must stay valid JSON.
        Assert.Contains(json ? "\\u001b" : "m\\u001b[2Jodel", all, json ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>A server whose answer holds an escape sequence in a header name: the exception it causes carries those bytes.</summary>
    [Theory]
    [InlineData("stt-test", false)]
    [InlineData("stt-test", true)]
    [InlineData("transcribe", false)]
    [InlineData("transcribe", true)]
    public async Task AServersMalformedAnswerReachesTheConsoleSpeltOut(string command, bool verbose)
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

                    // Take the rest of the upload, so that the client reads the answer rather than a reset.
                    using var drain = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    drain.CancelAfter(300);
                    try
                    {
                        while (await stream.ReadAsync(buffer, drain.Token) > 0)
                        {
                        }
                    }
                    catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                    {
                    }
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
            Audio.Wav.WritePcm16(stream, Enumerable.Range(0, 16_000).Select(i => 0.5f * MathF.Sin(i * 0.0549f)).ToArray(), 16_000);
        }

        string[] args = command == "transcribe" ? ["transcribe", wav, "--config", _configFile] : ["stt", "test", "remote", "--file", wav, "--config", _configFile];
        using var console = new ConsoleCapture();
        var code = await console.RunAsync(verbose ? [.. args, "--verbose"] : args);
        await cts.CancelAsync();
        listener.Stop();
        await Task.WhenAny(serving, Task.Delay(1000, TestContext.Current.CancellationToken));

        var all = console.Out.ToString() + console.Error;
        Assert.NotEqual(0, code);
        Assert.False(all.Contains('\u001b', StringComparison.Ordinal), all);
        Assert.False(all.Contains('\u0007', StringComparison.Ordinal), all);
        Assert.True(all.Contains("\\u001b", StringComparison.Ordinal), all);
        if (verbose && command == "transcribe")
        {
            Assert.Contains(Environment.NewLine + "   at ", console.Error.ToString(), StringComparison.Ordinal);
        }
    }
}
