using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Cli.Tests;

/// <summary>
/// What the commands that run a speech provider print when its plugin cannot start or answer: exit code 3 and the
/// hint, once, and any path that came from config.json spelt out rather than given raw to the terminal.
/// </summary>
public sealed class PluginFailureOutputTests : IDisposable
{
    private const string NotConfirmed = "Plugin test has not been confirmed on this computer.";

    private readonly TempDirectory _dir = new();
    private readonly string _configFile;
    private readonly PluginTrustStore _trust = new(AppPaths.FromEnvironment().PluginTrustFile, NullLog.Instance);

    public PluginFailureOutputTests(CliSandbox sandbox) => _configFile = sandbox.File($"failures-{Guid.NewGuid():N}.json");

    public void Dispose() => _dir.Dispose();

    /// <summary>A registered plugin that was never confirmed cannot start; every command that needs it ends the same way.</summary>
    [Theory]
    [InlineData("transcribe")]
    [InlineData("stt-test")]
    public async Task AProviderCommandWithAPluginThatCannotStartExits3WithTheHintOnce(string command)
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(folder, model: ModelFile());

        var (code, output, error) = await RunAsync(command == "transcribe" ? ["transcribe", WavFile()] : ["stt", "test", "p", "--file", WavFile()]);

        Assert.Equal(3, code);
        Assert.Equal("", output);
        Assert.Equal(1, Count(error, NotConfirmed));
        Assert.Equal(1, Count(error, $"dictum plugins add \"{folder}\""));
    }

    [Fact]
    public async Task ThePluginsThatCannotStartGoWithTheirLastLinesAfterTheHint()
    {
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["no CUDA here"] });
        _trust.Confirm("test", folder, PluginManifest.Load(folder).ExecutablePath(folder));
        SaveConfig(folder, model: ModelFile());

        var (code, _, error) = await RunAsync("transcribe", WavFile());

        Assert.Equal(3, code);
        Assert.Equal(1, Count(error, "exited with code 3 while starting"));
        Assert.Equal(1, Count(error, "no CUDA here"));
        Assert.Contains("  hint: ", error, StringComparison.Ordinal);
    }

    /// <summary>Doctor reports each provider as a row, so a plugin's failure is that row and nothing else.</summary>
    [Fact]
    public async Task DoctorShowsAPluginsFailureOnlyInItsRow()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(folder, model: ModelFile());

        var (_, output, error) = await RunAsync("doctor");

        Assert.Equal(1, Count(output, NotConfirmed));
        Assert.Equal(0, Count(error, NotConfirmed));
    }

    /// <summary>The refusal is a failure of the plugin, which the command reports as the error; nothing else says it too.</summary>
    [Fact]
    public async Task TheBatchFileRefusalIsPrintedOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows runs a batch file through cmd.exe");
        var folder = TestPlugins.BatchLauncherFolder(_dir, "plugin.cmd");
        _trust.Confirm("test", folder, PluginManifest.Load(folder).ExecutablePath(folder));
        SaveConfig(folder, model: Directory.CreateDirectory(_dir.File("R&D")).FullName);

        var (code, _, error) = await RunAsync("transcribe", WavFile());

        Assert.Equal(3, code);
        Assert.Equal(1, Count(error, "which cannot be given"));
        Assert.DoesNotContain("warning:", error, StringComparison.Ordinal);
    }

    /// <summary>The daemon answers with a failure's kind: a provider that is down ends the command with 3, like the local ones.</summary>
    [Theory]
    [InlineData("start")]
    [InlineData("toggle")]
    public async Task ADaemonCommandWhosePluginCannotStartExits3WithTheHintOnce(string command)
    {
        var message = "Plugin test has not been confirmed on this computer. Confirm it with 'dictum plugins add \"C:\\p\"'.";
        await using var server = new IpcServer(IpcEndpoint.SocketPath(AppPaths.FromEnvironment()), NullLog.Instance);
        server.Start((_, _) => Task.FromResult(IpcResponse.Failure(message, IpcFailureKind.ProviderUnavailable)));

        var (code, _, error) = await RunAsync(command);

        Assert.Equal(3, code);
        Assert.Equal($"error: {message}", error.TrimEnd());
    }

    /// <summary>An older daemon names no kind: the exit code is as it was.</summary>
    [Fact]
    public async Task ADaemonFailureWithoutAKindStillExits1()
    {
        await using var server = new IpcServer(IpcEndpoint.SocketPath(AppPaths.FromEnvironment()), NullLog.Instance);
        server.Start((_, _) => Task.FromResult(IpcResponse.Failure("It failed.")));

        var (code, _, error) = await RunAsync("start");

        Assert.Equal(1, code);
        Assert.Equal("error: It failed.", error.TrimEnd());
    }

    /// <summary>A plugin's own last line can hold anything; doctor's row spells it out.</summary>
    [Fact]
    public async Task APluginsLastLinesAreShownEscapedInDoctorsRow()
    {
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["\u001b[31mno CUDA\u001b[0m"] });
        _trust.Confirm("test", folder, PluginManifest.Load(folder).ExecutablePath(folder));
        SaveConfig(folder, model: ModelFile());

        var (_, output, _) = await RunAsync("doctor");

        Assert.False(Dirty(output), output);
        Assert.Contains("last output: \\u001b[31mno CUDA\\u001b[0m", output, StringComparison.Ordinal);
    }

    /// <summary>A copied config can carry anything in a path; the terminal gets it spelt out.</summary>
    [Theory]
    [InlineData("\u001b[2J", "\\u001b[2J")]
    [InlineData("\r", "\\u000d")]
    [InlineData("\u202e", "\\u202e")]
    [InlineData("\udb40\udc01", "\\U000e0001")]
    public async Task APluginsPathFromTheConfigIsShownEscapedByList(string hidden, string shown)
    {
        SaveConfig(Weird(hidden), model: ModelFile());

        var (code, output, _) = await RunAsync("plugins", "list");

        Assert.Equal(0, code);
        Assert.False(Dirty(output), output);
        Assert.Contains($"a{shown}b", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u001b[2J", "\\u001b[2J")]
    [InlineData("\u202e", "\\u202e")]
    public async Task APluginsPathFromTheConfigIsShownEscapedByRemove(string hidden, string shown)
    {
        SaveConfig(Weird(hidden), withProvider: false);

        var (code, output, _) = await RunAsync("plugins", "remove", "test");

        Assert.Equal(0, code);
        Assert.False(Dirty(output), output);
        Assert.Contains($"a{shown}b", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u001b[2J", "\\u001b[2J")]
    [InlineData("\u202e", "\\u202e")]
    public async Task APluginsPathFromTheConfigIsShownEscapedByAdd(string hidden, string shown)
    {
        SaveConfig(Weird(hidden), withProvider: false);

        var (code, _, error) = await RunAsync("plugins", "add", TestPlugins.Folder(_dir, folderName: "other"), "--yes");

        Assert.Equal(2, code);
        Assert.False(Dirty(error), error);
        Assert.Contains($"a{shown}b", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u001b[2J", "\\u001b[2J")]
    [InlineData("\u202e", "\\u202e")]
    public async Task APluginsPathFromTheConfigIsShownEscapedWhereAProviderCannotUseIt(string hidden, string shown)
    {
        SaveConfig(Weird(hidden), model: ModelFile());

        var (code, _, error) = await RunAsync("stt", "test", "p", "--file", WavFile());

        Assert.Equal(3, code);
        Assert.False(Dirty(error), error);
        Assert.Contains($"a{shown}b", error, StringComparison.Ordinal);
    }

    /// <summary>A name with a right-to-left override is a real folder on every system, so its hint is the real one.</summary>
    [Fact]
    public async Task AFolderWithAnInvisibleCharacterIsShownEscapedInTheHint()
    {
        var folder = TestPlugins.Folder(_dir, folderName: "a\u202eb");
        SaveConfig(folder, model: ModelFile());

        var (code, _, error) = await RunAsync("transcribe", WavFile());

        Assert.Equal(3, code);
        Assert.False(Dirty(error), error);
        Assert.Contains("a\\u202eb", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelPathFromTheConfigIsShownEscapedWhereTheModelIsMissing()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(folder, model: _dir.File("m\u202eodel"));

        var (code, _, error) = await RunAsync("stt", "test", "p", "--file", WavFile());

        Assert.Equal(3, code);
        Assert.False(Dirty(error), error);
        Assert.Contains("m\\u202eodel", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SttListShowsAModelPathFromTheConfigEscaped()
    {
        SaveConfig(TestPlugins.Folder(_dir), model: _dir.File("m\u001b[2Jodel"));

        var (_, output, _) = await RunAsync("stt", "list");

        Assert.False(Dirty(output), output);
        Assert.Contains("m\\u001b[2Jodel", output, StringComparison.Ordinal);
    }

    /// <summary>A folder in the registration that no longer reads: the row says so, once, and not as a plugin.json that is wrong.</summary>
    [Fact]
    public async Task AnUnreadablePluginJsonIsNotCalledInvalid()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(folder, model: ModelFile());
        using var held = new FileStream(Path.Combine(folder, PluginProtocol.ManifestFile), FileMode.Open, FileAccess.Read, FileShare.None);

        var (code, output, _) = await RunAsync("plugins", "list");

        Assert.Equal(0, code);
        Assert.Contains("plugin.json cannot be read: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not valid", output, StringComparison.Ordinal);
        Assert.Equal(1, Count(output, "cannot be read"));
    }

    private string Weird(string hidden) => Path.Combine(_dir.Path, $"a{hidden}b");

    private string ModelFile()
    {
        var model = _dir.File("model.bin");
        File.WriteAllText(model, "model");
        return model;
    }

    /// <summary>One second of a tone as a 16 kHz mono WAV, which the pipeline reads before it asks the provider.</summary>
    private string WavFile()
    {
        var path = _dir.File("sample.wav");
        using var stream = File.Create(path);
        Audio.Wav.WritePcm16(stream, Enumerable.Range(0, 16_000).Select(i => 0.5f * MathF.Sin(i * 0.0549f)).ToArray(), 16_000);
        return path;
    }

    /// <summary>Whether any line of <paramref name="text"/> holds a character a terminal would act on.</summary>
    private static bool Dirty(string text) => text.Split('\n').Any(line => PluginText.HasHidden(line.TrimEnd('\r')));

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private void SaveConfig(string folder, string? model = null, bool withProvider = true)
    {
        var config = new DictumConfig { Plugins = { ["test"] = new PluginRegistration { Path = folder } } };
        if (withProvider)
        {
            config.Stt["p"] = new PluginSttConfig { Plugin = "test", Model = model! };
            config.Profiles.Add(new ProfileConfig { Id = "p", Stt = "p" });
        }

        new ConfigStore(_configFile, NullLog.Instance).Save(config);
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var console = new ConsoleCapture();
        var code = await console.RunAsync([.. args, "--config", _configFile]);
        return (code, console.Out.ToString(), console.Error.ToString());
    }
}
