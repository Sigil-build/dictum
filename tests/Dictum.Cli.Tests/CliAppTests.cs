using System.CommandLine;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Setup;
using Dictum.Testing;

namespace Dictum.Cli.Tests;

/// <summary>
/// The exit codes scripts rely on: 0 done, 1 error, 2 invalid config, 3 provider unavailable, 4 daemon not running,
/// 5 download failed, 130 interrupted.
/// </summary>
public sealed class CliAppTests(CliSandbox sandbox)
{
    private static readonly Dictionary<string, Func<Exception>> Failures = new()
    {
        ["invalid config"] = () => new ConfigException("The config is not valid."),
        ["provider unavailable"] = () => new ProviderUnavailableException("The server at http://localhost:9 is not answering."),
        ["http failure"] = () => new HttpRequestException("Connection refused."),
        ["daemon not running"] = () => new DaemonNotRunningException("The daemon is not running."),
        ["download failed"] = () => new DownloadException("The download stopped."),
        ["microphone"] = () => new AudioDeviceException("Microphone not found.", "Run 'dictum devices'."),
        ["daemon already running"] = () => new DaemonAlreadyRunningException("A daemon is already running."),
        ["bad wav"] = () => new WavFormatException("Not a WAV file."),
        ["missing file"] = () => new FileNotFoundException("No such file."),
        ["missing folder"] = () => new DirectoryNotFoundException("No such folder."),
        ["anything else"] = () => new InvalidOperationException("Something broke."),
    };

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("invalid config", 2)]
    [InlineData("provider unavailable", 3)]
    [InlineData("http failure", 3)]
    [InlineData("daemon not running", 4)]
    [InlineData("download failed", 5)]
    [InlineData("microphone", 1)]
    [InlineData("daemon already running", 1)]
    [InlineData("bad wav", 1)]
    [InlineData("missing file", 1)]
    [InlineData("missing folder", 1)]
    [InlineData("anything else", 1)]
    public async Task EachFailureEndsWithItsExitCodeAndAnErrorLine(string failure, int exitCode)
    {
        using var console = new ConsoleCapture();
        var thrown = Failures[failure]();

        var code = await RunAsync(thrown);

        Assert.Equal(exitCode, code);
        Assert.StartsWith($"error: {thrown.Message}", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidConfigListsItsIssues()
    {
        using var console = new ConsoleCapture();
        ConfigIssue[] issues = [new(ConfigIssueSeverity.Error, "profiles[0].stt", "unknown provider 'groq'")];

        var code = await RunAsync(new ConfigException("The config is not valid.", issues));

        Assert.Equal(2, code);
        Assert.Contains("  profiles[0].stt: unknown provider 'groq'", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureWithAHintPrintsTheHint()
    {
        using var console = new ConsoleCapture();

        await RunAsync(new AudioDeviceException("Microphone not found.", "Run 'dictum devices'."));

        Assert.Contains("  hint: Run 'dictum devices'.", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedDictationEndsWithTheExitCodeOfItsCause()
    {
        using var console = new ConsoleCapture();
        var cause = new DaemonNotRunningException("The daemon is not running.");

        var code = await RunAsync(new DictationException("Dictation failed.", "transcribe", rawText: null, cause));

        Assert.Equal(4, code);
        Assert.StartsWith("error: The daemon is not running.", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedFailurePointsToVerbose()
    {
        using var console = new ConsoleCapture();

        await RunAsync(new InvalidOperationException("Something broke."));

        Assert.Contains("(run with --verbose for details)", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithVerboseAnUnexpectedFailurePrintsTheException()
    {
        using var console = new ConsoleCapture();

        var code = await RunAsync(new InvalidOperationException("Something broke."), "--verbose");

        Assert.Equal(1, code);
        Assert.Contains(typeof(InvalidOperationException).FullName!, console.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("(run with --verbose for details)", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CtrlCEndsWith130()
    {
        using var console = new ConsoleCapture();
        using var interrupted = new CancellationTokenSource();
        await interrupted.CancelAsync();

        var code = await CliApp.RunAsync(Parse(), (_, ct) => Task.FromCanceled<int>(ct), interrupted.Token);

        Assert.Equal(130, code);
        Assert.Equal("Interrupted.", console.Error.ToString().Trim());
    }

    [Fact]
    public async Task ACancellationNobodyAskedForIsAnError()
    {
        using var console = new ConsoleCapture();

        var code = await RunAsync(new OperationCanceledException("The operation was canceled."));

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task ACommandThatSucceedsEndsWith0()
    {
        using var console = new ConsoleCapture();

        var code = await CliApp.RunAsync(Parse(), (_, _) => Task.FromResult(0), _ct);

        Assert.Equal(0, code);
        Assert.Empty(console.Error.ToString());
    }

    [Fact]
    public async Task TheConfigPathIsInTheSandbox()
    {
        using var console = new ConsoleCapture();

        Assert.Equal(0, await console.RunAsync("config", "path"));
        Assert.Equal(Path.Combine(sandbox.ConfigDirectory, "config.json"), console.Out.ToString().Trim());
    }

    [Fact]
    public async Task StatusWithoutADaemonEndsWith4()
    {
        using var console = new ConsoleCapture();

        Assert.Equal(4, await console.RunAsync("status"));
        Assert.StartsWith("error: ", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfigFileThatIsNotJsonEndsWith2()
    {
        var file = sandbox.File("broken.json");
        await File.WriteAllTextAsync(file, "{ not json", _ct);
        using var console = new ConsoleCapture();

        Assert.Equal(2, await console.RunAsync("config", "validate", "--config", file));
    }

    // After the pull the message suggests, the starter config's model is there, so doctor does not report it missing.
    [Fact]
    public async Task ConfigInitNamesTheModelItsMessageTellsYouToPull()
    {
        var file = sandbox.File("starter.json");
        using var console = new ConsoleCapture();

        Assert.Equal(0, await console.RunAsync("config", "init", "--config", file));

        var pull = System.Text.RegularExpressions.Regex.Match(console.Error.ToString(), @"dictum models pull ([\w.-]+)");
        Assert.True(pull.Success, console.Error.ToString());
        var config = ConfigStore.Parse(await File.ReadAllTextAsync(file, _ct));
        var model = Assert.IsType<WhisperLocalSttConfig>(config.Stt[DefaultConfig.LocalSttId]).Model;
        Assert.Equal(Catalog.BuiltIn.Get(pull.Groups[1].Value).File, model);
    }

    [Fact]
    public async Task ModelsFolderPrintsTheDefault()
    {
        using var console = new ConsoleCapture();

        Assert.Equal(0, await console.RunAsync("models", "folder", "--config", sandbox.File("none.json")));
        Assert.Equal(sandbox.File("data"), console.Out.ToString().Trim());
    }

    [Fact]
    public async Task ModelsFolderWithoutAConfigPointsAtInit()
    {
        using var dir = new TempDirectory();
        using var console = new ConsoleCapture();

        Assert.Equal(ExitCodes.InvalidConfig, await console.RunAsync("models", "folder", dir.File("new"), "--config", dir.File("config.json")));
        Assert.Contains("dictum init --downloads-dir", console.Error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(dir.File("new")));
    }

    [Fact]
    public async Task ModelsFolderMovesWithMove()
    {
        using var dir = new TempDirectory();
        var file = await ConfigWithDownloadsAsync(dir, dir.File("old"));
        var model = await ModelAsync(dir.File("old"), "m.bin");
        var target = dir.File("new");
        using var console = new ConsoleCapture();

        var code = await console.RunAsync("models", "folder", target, "--move", "--config", file);

        Assert.True(code == 0, console.Error.ToString());
        Assert.Equal("model", await File.ReadAllTextAsync(Path.Combine(target, "models", "m.bin"), _ct));
        Assert.False(File.Exists(model));
        Assert.Equal(target, ConfigStore.Parse(await File.ReadAllTextAsync(file, _ct)).General.DownloadsDirectory);
    }

    [Fact]
    public async Task ModelsFolderWithoutMoveLeavesTheFilesAndSaysSo()
    {
        using var dir = new TempDirectory();
        var file = await ConfigWithDownloadsAsync(dir, dir.File("old"));
        var model = await ModelAsync(dir.File("old"), "m.bin");
        using var console = new ConsoleCapture();

        Assert.Equal(0, await console.RunAsync("models", "folder", dir.File("new"), "--config", file));

        Assert.True(File.Exists(model));
        Assert.Contains("stay in", console.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(dir.File("new"), ConfigStore.Parse(await File.ReadAllTextAsync(file, _ct)).General.DownloadsDirectory);
    }

    [Fact]
    public async Task ModelsFolderResetMovesTheModelsBackToTheDataFolder()
    {
        using var dir = new TempDirectory();
        var file = await ConfigWithDownloadsAsync(dir, dir.File("old"));
        await ModelAsync(dir.File("old"), "reset.bin");
        using var console = new ConsoleCapture();

        var code = await console.RunAsync("models", "folder", "--reset", "--move", "--config", file);

        Assert.True(code == 0, console.Error.ToString());
        Assert.True(File.Exists(Path.Combine(sandbox.File("data"), "models", "reset.bin")));
        Assert.Null(ConfigStore.Parse(await File.ReadAllTextAsync(file, _ct)).General.DownloadsDirectory);
        File.Delete(Path.Combine(sandbox.File("data"), "models", "reset.bin"));
    }

    [Fact]
    public async Task ModelsDeleteRefusesAModelInUse()
    {
        using var dir = new TempDirectory();
        var file = await ConfigWithDownloadsAsync(dir, dir.Path);
        var model = await ModelAsync(dir.Path, DefaultConfig.DefaultModel);
        using var console = new ConsoleCapture();

        Assert.Equal(ExitCodes.InvalidConfig, await console.RunAsync("models", "delete", Catalog.BuiltIn.ForFile(DefaultConfig.DefaultModel)!.Id, "--config", file));

        Assert.True(File.Exists(model));
        Assert.Contains("is used by profile(s) dictate", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsDeleteRemovesAnUnusedModel()
    {
        using var dir = new TempDirectory();
        var file = await ConfigWithDownloadsAsync(dir, dir.Path);
        var model = await ModelAsync(dir.Path, Catalog.BuiltIn.Get("whisper-medium-q8").File);
        using var console = new ConsoleCapture();

        Assert.Equal(0, await console.RunAsync("models", "delete", "whisper-medium-q8", "whisper-tiny-en", "--config", file));

        Assert.False(File.Exists(model));
        Assert.Contains("whisper-tiny-en is not installed", console.Error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The starter config with <paramref name="downloads"/> as its downloads folder, in <paramref name="dir"/>.</summary>
    private async Task<string> ConfigWithDownloadsAsync(TempDirectory dir, string downloads)
    {
        var file = dir.File("config.json");
        var config = DefaultConfig.Create() with { General = new GeneralSettings { DownloadsDirectory = downloads } };
        await File.WriteAllTextAsync(file, ConfigStore.Serialize(config), _ct);
        return file;
    }

    private async Task<string> ModelAsync(string downloads, string name)
    {
        var path = Path.Combine(downloads, "models", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "model", _ct);
        return path;
    }

    private static ParseResult Parse(params string[] args) => CliApp.BuildRoot().Parse(args);

    private Task<int> RunAsync(Exception thrown, params string[] args) =>
        CliApp.RunAsync(Parse(args), (_, _) => Task.FromException<int>(thrown), _ct);
}
