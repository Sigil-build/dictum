using Dictum.Cli.Commands;
using Dictum.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Host.Ipc;
using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Cli.Tests;

/// <summary>What an exception from a remote server, or the log, puts on the console: spelt out, never raw.</summary>
public sealed class ConsoleSinksTests : IDisposable
{
    /// <summary>The zero-width non-joiner.</summary>
    private const char Joiner = (char)0x200C;

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
        await using var server = new IpcServer(IpcEndpoint.SocketPath(AppPaths.FromEnvironment()), NullLog.Instance);
        server.Start((_, _) => Task.FromResult(command == "start"
            ? IpcResponse.Failure("no \u001b[2Jprovider")
            : IpcResponse.Success(new DaemonStatus(
                "Id\u001b[2Jle", "act\u001b[2Jive", [new ProfileStatus("p\u001b[2Jid", "Ctrl+A", "tog\u001b[2Jgle", true)], "hk", "inj", 1, Warning: "w\u001b[2Jarn"))));

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
        await using var server = new RawServer("HTTP/1.1 200 OK\r\nX\u001b]0;PWNED\u0007: v\r\nContent-Length: 0\r\n\r\n");
        SaveRemoteProvider(server.Url);
        var args = RemoteRequest(command);

        using var console = new ConsoleCapture();
        var code = await console.RunAsync(verbose ? [.. args, "--verbose"] : args);

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

    /// <summary>An error's body is the server's own text, which every report of it quotes.</summary>
    [Fact]
    public async Task AServersErrorBodyReachesTheConsoleSpeltOut()
    {
        const string Body = "the \u001b[2Jmodel is gone";
        await using var server = new RawServer($"HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: {Body.Length}\r\n\r\n{Body}");
        SaveRemoteProvider(server.Url);

        using var console = new ConsoleCapture();
        var code = await console.RunAsync(RemoteRequest("transcribe"));

        Assert.NotEqual(0, code);
        AssertSpeltOut(console, "the \\u001b[2Jmodel is gone");
    }

    [Fact]
    public async Task AProfilesWordsArePrintedSpeltOut()
    {
        SaveProfile(vocabulary: ["w\u001b[2Jord"]);

        using var console = new ConsoleCapture(terminal: true);
        Assert.Equal(0, await console.RunAsync(["vocab", "list", "--config", _configFile]));

        AssertSpeltOut(console, "w\\u001b[2Jord");
    }

    [Fact]
    public async Task AWordLeftOutOfTheSpeechHintIsNamedSpeltOut()
    {
        SaveProfile(vocabulary: [.. Enumerable.Range(0, 400).Select(i => $"word{i}"), "l\u001b[2Jast"]);

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["vocab", "list", "--config", _configFile]));

        Assert.False(console.Error.ToString().Contains('\u001b', StringComparison.Ordinal), console.Error.ToString());
        Assert.Contains("l\\u001b[2Jast", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWordThatIsNotInTheListIsNamedSpeltOut()
    {
        SaveProfile(vocabulary: ["word"]);

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["vocab", "remove", "x\u001b[2Jy", "--config", _configFile]));

        AssertSpeltOut(console, "not in the list: x\\u001b[2Jy");
    }

    [Fact]
    public async Task AProfilesIdIsNamedSpeltOutAfterAnEdit()
    {
        SaveProfile(id: "p\u001b[2Jid", vocabulary: ["word"]);

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["vocab", "clear", "--profile", "p\u001b[2Jid", "--config", _configFile]));

        AssertSpeltOut(console, "p\\u001b[2Jid: 0 word(s)");
    }

    [Fact]
    public async Task AHotkeyChangeNamesTheProfileSpeltOut()
    {
        SaveProfile(id: "p\u001b[2Jid");

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["profiles", "set-hotkey", "p\u001b[2Jid", "Ctrl+Alt+K", "--config", _configFile]));

        AssertSpeltOut(console, "p\\u001b[2Jid: ");
    }

    /// <summary>When the secret store answers, the name goes back as it was typed only spelt out.</summary>
    [Fact]
    public async Task ASecretsNameIsNamedSpeltOut()
    {
        using var console = new ConsoleCapture();
        _ = await console.RunAsync(["secret", "check", "n\u001b[2Jame"]);
        Assert.SkipUnless(console.Error.ToString().Contains(" stored", StringComparison.Ordinal), "No secret store answers here, so nothing names the secret");

        AssertSpeltOut(console, "n\\u001b[2Jame");
    }

    /// <summary>A file name in the models folder is whatever put it there chose; Windows allows this character in one.</summary>
    [Fact]
    public async Task AFileInTheModelsFolderIsListedSpeltOut()
    {
        var models = AppPaths.FromEnvironment().ModelsDirectory;
        Directory.CreateDirectory(models);
        var file = Path.Combine(models, "m\u202eodel.bin");
        File.WriteAllBytes(file, [1]);
        try
        {
            using var console = new ConsoleCapture();
            Assert.Equal(0, await console.RunAsync(["models", "list"]));

            AssertSpeltOut(console, "m\\u202eodel.bin", hidden: '\u202e');
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("list")]
    public async Task ADownloadsFolderFromTheConfigIsPrintedSpeltOut(string command)
    {
        var config = new DictumConfig { General = new GeneralSettings { DownloadsDirectory = Path.Combine(Path.GetTempPath(), "d\u001b[2Jl") } };
        new ConfigStore(_configFile, NullLog.Instance).Save(config);

        using var console = new ConsoleCapture(terminal: true);
        Assert.Equal(0, await console.RunAsync(["models", command, "--config", _configFile]));

        AssertSpeltOut(console, "d\\u001b[2Jl");
    }

    /// <summary>
    /// A script reads these as values: a joiner spelt out would name another word or folder, and a terminal is not there
    /// to be harmed. A joiner is a letter's business in Persian and in Indic scripts, so such values are real.
    /// </summary>
    [Fact]
    public async Task AWordReachesAScriptWhole()
    {
        var word = $"w{Joiner}ord";
        SaveProfile(vocabulary: [word]);

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["vocab", "list", "--config", _configFile]));

        Assert.Equal(word, console.Out.ToString().Trim());
    }

    [Fact]
    public async Task ADownloadsFolderReachesAScriptWhole()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"d{Joiner}l");
        new ConfigStore(_configFile, NullLog.Instance).Save(new DictumConfig { General = new GeneralSettings { DownloadsDirectory = folder } });

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["models", "folder", "--config", _configFile]));

        Assert.Equal(folder, console.Out.ToString().Trim());
    }

    [Fact]
    public async Task ADeletedModelsPathReachesAScriptWhole()
    {
        var folder = _dir.File($"d{Joiner}l");
        new ConfigStore(_configFile, NullLog.Instance).Save(new DictumConfig { General = new GeneralSettings { DownloadsDirectory = folder } });
        var entry = Catalog.BuiltIn.Entries[0];
        var file = AppPaths.FromEnvironment().WithDownloads(folder).ResolveModel(entry.File);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, [1]);

        using var console = new ConsoleCapture();
        Assert.Equal(0, await console.RunAsync(["models", "delete", entry.Id, "--config", _configFile]));

        Assert.Equal(file, console.Out.ToString().Trim());
    }

    [Fact]
    public async Task AHistoryEntryIsPrintedSpeltOut()
    {
        var store = new HistoryStore(AppPaths.FromEnvironment().HistoryFile, 10);
        store.Append(new HistoryEntry(DateTimeOffset.Now, "p\u001b[2Jid", "s\u001b[2Jtt", "r", "t\u001b[2Jext", null, 1, "e\u001b[2Jrror"));
        try
        {
            using var console = new ConsoleCapture();
            Assert.Equal(0, await console.RunAsync(["history", "--last", "1"]));

            AssertSpeltOut(console, "p\\u001b[2Jid/s\\u001b[2Jtt  t\\u001b[2Jext  [failed: e\\u001b[2Jrror]");
        }
        finally
        {
            File.Delete(store.Path);
        }
    }

    [Fact]
    public async Task AProviderFromTheConfigIsListedSpeltOut()
    {
        var config = new DictumConfig();
        config.Stt["s\u001b[2Jtt"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:1/v\u001b[2J1", Model = "m\u001b[2Jodel" };
        new ConfigStore(_configFile, NullLog.Instance).Save(config);

        using var console = new ConsoleCapture();
        _ = await console.RunAsync(["stt", "list", "--config", _configFile]);

        AssertSpeltOut(console, "s\\u001b[2Jtt");
    }

    [Fact]
    public void AMicrophoneIsListedSpeltOut()
    {
        var line = DevicesCommand.Line(new AudioDevice("1", "m\u001b[2Jic", "h\u001b[2Jost", 2, 48_000, IsDefault: true));

        Assert.False(line.Contains('\u001b', StringComparison.Ordinal), line);
        Assert.Contains("m\\u001b[2Jic  (h\\u001b[2Jost, ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDaemonsSummaryIsPrintedSpeltOut()
    {
        var status = new DaemonStatus(
            "Idle",
            null,
            [new ProfileStatus("p\u001b[2Jid", "Ctrl+\u001b[2JA", "Tog\u001b[2Jgle", Registered: false)],
            "h\u001b[2Jk",
            "i\u001b[2Jnj",
            1,
            Warning: "w\u001b[2Jarn");

        using var console = new ConsoleCapture();
        await using (var context = new CliContext(CliApp.BuildRoot().Parse(["daemon"]), LogLevel.Info))
        {
            DaemonCommand.PrintSummary(context, status);
        }

        var all = console.Error.ToString();
        Assert.False(all.Contains('\u001b', StringComparison.Ordinal), all);
        foreach (var shown in new[] { "hotkeys: h\\u001b[2Jk", "paste:   i\\u001b[2Jnj", "p\\u001b[2Jid", "Ctrl+\\u001b[2JA (Tog\\u001b[2Jgle, not registered)", "warning: w\\u001b[2Jarn" })
        {
            Assert.Contains(shown, all, StringComparison.Ordinal);
        }
    }

    private static void AssertSpeltOut(ConsoleCapture console, string shown, char hidden = '\u001b')
    {
        var all = console.Out.ToString() + console.Error;
        Assert.False(all.Contains(hidden, StringComparison.Ordinal), all);
        Assert.Contains(shown, all, StringComparison.Ordinal);
    }

    private void SaveProfile(string id = "p", IReadOnlyList<string>? vocabulary = null)
    {
        var config = new DictumConfig();
        config.Stt["s"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:1/v1", Model = "m" };
        config.Profiles.Add(new ProfileConfig { Id = id, Stt = "s", Language = "uk", Vocabulary = [.. vocabulary ?? []] });
        new ConfigStore(_configFile, NullLog.Instance).Save(config);
    }

    private void SaveRemoteProvider(string url)
    {
        var config = new DictumConfig();
        config.Stt["remote"] = new OpenAiCompatibleSttConfig { BaseUrl = url, Model = "m" };
        config.Profiles.Add(new ProfileConfig { Id = "remote", Stt = "remote" });
        new ConfigStore(_configFile, NullLog.Instance).Save(config);
    }

    /// <summary>The command, on a short tone that the provider of <see cref="SaveRemoteProvider"/> is asked to transcribe.</summary>
    private string[] RemoteRequest(string command)
    {
        var wav = _dir.File("sample.wav");
        using (var stream = File.Create(wav))
        {
            Audio.Wav.WritePcm16(stream, Enumerable.Range(0, 16_000).Select(i => 0.5f * MathF.Sin(i * 0.0549f)).ToArray(), 16_000);
        }

        return command == "transcribe" ? ["transcribe", wav, "--config", _configFile] : ["stt", "test", "remote", "--file", wav, "--config", _configFile];
    }

    /// <summary>A server that gives every request this answer, byte for byte, however wrong it is.</summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _answer;
        private readonly Task _serving;

        public RawServer(string answer)
        {
            _answer = Encoding.Latin1.GetBytes(answer);
            _listener.Start();
            _serving = Task.Run(ServeAsync, TestContext.Current.CancellationToken);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await Task.WhenAny(_serving, Task.Delay(1000, TestContext.Current.CancellationToken));
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    await stream.WriteAsync(_answer, _stop.Token);

                    // Take the rest of the upload, so that the client reads the answer rather than a reset.
                    using var drain = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    drain.CancelAfter(300);
                    try
                    {
                        while (await stream.ReadAsync(buffer, drain.Token) > 0)
                        {
                        }
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }
        }
    }
}
