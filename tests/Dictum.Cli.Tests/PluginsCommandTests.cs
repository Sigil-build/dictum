using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;
using Dictum.TestPlugin;

namespace Dictum.Cli.Tests;

/// <summary><c>dictum plugins list | add | remove</c> against the real test plugin, in the sandbox's folders.</summary>
public sealed class PluginsCommandTests : IDisposable
{
    private const string Trust = "This plugin is a program that runs on your computer. Dictum did not check it. Add it only if you trust where it came from.";

    private readonly TempDirectory _dir = new();
    private readonly string _configFile;
    private readonly PluginTrustStore _trust = new(AppPaths.FromEnvironment().PluginTrustFile, NullLog.Instance);

    public PluginsCommandTests(CliSandbox sandbox) => _configFile = sandbox.File($"plugins-{Guid.NewGuid():N}.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AddWithYesConfirmsAndRegistersThePlugin()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());

        var (code, output, error) = await RunAsync("add", folder, "--yes");

        Assert.Equal(0, code);
        Assert.Equal(folder, LoadConfig().Plugins["test"].Path);
        Assert.Equal(PluginTrust.Confirmed, Check(folder));
        Assert.Contains(Trust, error, StringComparison.Ordinal);
        Assert.Contains($"Test 1.0.0 by an unknown publisher from {folder}", error, StringComparison.Ordinal);
        Assert.Contains("Added Test 1.0.0 (protocol 1).", output, StringComparison.Ordinal);
        Assert.False(TestPlugins.RunsIn(_dir));
    }

    [Fact]
    public async Task AddStoresTheFolderTheWayTheDaemonSpellsIt()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());

        var (code, _, _) = await RunAsync("add", folder + Path.DirectorySeparatorChar, "--yes");

        Assert.Equal(0, code);
        Assert.Equal(folder, LoadConfig().Plugins["test"].Path);
    }

    [Fact]
    public async Task AddWithoutYesOnARedirectedInputChangesNothing()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());

        var (code, _, error) = await RunAsync("add", folder);

        Assert.Equal(2, code);
        Assert.Contains("Input is not a terminal", error, StringComparison.Ordinal);
        Assert.Empty(LoadConfig().Plugins);
        Assert.Equal(PluginTrust.NotConfirmed, Check(folder));
    }

    [Fact]
    public async Task AddWithoutAConfigRefusesBeforeReadingThePlugin()
    {
        var (code, _, error) = await RunAsync("add", _dir.File("nowhere"), "--yes");

        Assert.Equal(2, code);
        Assert.Contains("No config yet; run 'dictum init' first.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidManifestIsRefusedWithItsProblems()
    {
        var folder = TestPlugins.Folder(_dir);
        File.WriteAllText(
            Path.Combine(folder, PluginProtocol.ManifestFile),
            """{ "protocol": 2, "id": "test", "name": "Test", "version": "1.0.0" }""");
        SaveConfig(new DictumConfig());

        var (code, _, error) = await RunAsync("add", folder, "--yes");

        Assert.Equal(2, code);
        Assert.Contains("needs protocol 2", error, StringComparison.Ordinal);
        Assert.Empty(LoadConfig().Plugins);
    }

    [Fact]
    public async Task APluginThatCannotStartIsNotAdded()
    {
        var folder = TestPlugins.Folder(_dir, behaviour: new TestPluginBehaviour { ExitAtStart = 3, Stderr = ["no CUDA here"] });
        SaveConfig(new DictumConfig());

        var (code, _, error) = await RunAsync("add", folder, "--yes");

        Assert.Equal(1, code);
        Assert.Contains("  | no CUDA here", error, StringComparison.Ordinal);
        Assert.Contains("hint:", error, StringComparison.Ordinal);
        Assert.Empty(LoadConfig().Plugins);
        Assert.Equal(PluginTrust.NotConfirmed, Check(folder));
    }

    [Fact]
    public async Task ASecondFolderWithTheSameIdIsRefused()
    {
        var first = TestPlugins.Folder(_dir, folderName: "first");
        var second = TestPlugins.Folder(_dir, folderName: "second");
        SaveConfig(new DictumConfig { Plugins = { ["test"] = new PluginRegistration { Path = first } } });

        var (code, _, error) = await RunAsync("add", second, "--yes");

        Assert.Equal(2, code);
        Assert.Contains($"A plugin named test is already added from {first}.", error, StringComparison.Ordinal);
        Assert.Equal(first, LoadConfig().Plugins["test"].Path);
    }

    [Fact]
    public async Task AddingAgainConfirmsTheChangedProgram()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());
        await RunAsync("add", folder, "--yes");
        var manifest = PluginManifest.Load(folder);
        await using (var program = File.Open(manifest.ExecutablePath(folder), FileMode.Append))
        {
            program.WriteByte(0);
        }

        Assert.Equal(PluginTrust.Changed, Check(folder));

        var (code, _, error) = await RunAsync("add", folder, "--yes");

        Assert.Equal(0, code);
        Assert.Equal(PluginTrust.Confirmed, Check(folder));
        Assert.Contains("test is already added; confirming it again.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconfirmingTheSameFolderLeavesTheConfigUntouched()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());
        await RunAsync("add", folder, "--yes");
        var before = File.ReadAllBytes(_configFile);
        var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_configFile, old);
        string[] spellings = OperatingSystem.IsWindows()
            ? [folder + Path.DirectorySeparatorChar, folder.ToUpperInvariant()]
            : [folder + Path.DirectorySeparatorChar];

        foreach (var spelling in spellings)
        {
            File.AppendAllText(PluginManifest.Load(folder).ExecutablePath(folder), "x");
            Assert.Equal(PluginTrust.Changed, Check(folder));

            var (code, _, _) = await RunAsync("add", spelling, "--yes");

            Assert.Equal(0, code);
            Assert.Equal(PluginTrust.Confirmed, Check(folder));
            Assert.Equal(before, File.ReadAllBytes(_configFile));
            Assert.Equal(old, File.GetLastWriteTimeUtc(_configFile));
        }
    }

    [Fact]
    public async Task AManifestWithAnEscapeSequenceIsRefusedBeforeAnyQuestion()
    {
        var folder = TestPlugins.Folder(_dir);
        var executable = PluginManifest.Load(folder).ExecutablePath(folder);
        var manifest = Path.Combine(folder, PluginProtocol.ManifestFile);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Test\"", "\"Test\\u001b[2J\"", StringComparison.Ordinal));
        SaveConfig(new DictumConfig());

        var (code, _, error) = await RunAsync("add", folder, "--yes");

        Assert.Equal(2, code);
        Assert.Contains("name must not contain control characters", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', error);
        Assert.DoesNotContain(Trust, error, StringComparison.Ordinal);
        Assert.Empty(LoadConfig().Plugins);
        Assert.Equal(PluginTrust.NotConfirmed, _trust.Check("test", folder, executable));
    }

    [Fact]
    public async Task RemoveRefusesAPluginAProviderUses()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(WithProvider(folder));

        var (code, _, error) = await RunAsync("remove", "test");

        Assert.Equal(2, code);
        Assert.Contains("test is used by the speech provider(s) p; change or remove them first.", error, StringComparison.Ordinal);
        Assert.Contains("test", LoadConfig().Plugins.Keys);
    }

    [Fact]
    public async Task RemoveRefusesAnIdThatIsNotAdded()
    {
        SaveConfig(new DictumConfig());

        var (code, _, error) = await RunAsync("remove", "test");

        Assert.Equal(2, code);
        Assert.Contains("test is not added.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveUnregistersAndLeavesTheFolder()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());
        await RunAsync("add", folder, "--yes");

        var (code, output, _) = await RunAsync("remove", "test");

        Assert.Equal(0, code);
        Assert.Empty(LoadConfig().Plugins);
        Assert.Equal(PluginTrust.NotConfirmed, Check(folder));
        Assert.True(Directory.Exists(folder));
        Assert.Contains($"Removed test. Its folder {folder} was left as it was.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListShowsWhisperAndEachAddedPlugin()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(new DictumConfig());
        await RunAsync("add", folder, "--yes");

        var (_, text, _) = await RunAsync("list");
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Matches(@"^whisper\s+Whisper\s+built in$", lines[0]);
        Assert.Equal($"test  Test 1.0.0  your own · {folder} · confirmed", lines[1]);

        File.AppendAllText(PluginManifest.Load(folder).ExecutablePath(folder), "x");
        var (_, changed, _) = await RunAsync("list");
        Assert.Contains("changed since you added it", changed, StringComparison.Ordinal);

        var (_, json, _) = await RunAsync("list", "--json");
        var rows = JsonSerializer.Deserialize(json, CliJsonContext.Readable.ListPluginJson)!;
        Assert.Equal(["whisper", "test"], rows.Select(r => r.Id));
        Assert.Equal(["built-in", "custom"], rows.Select(r => r.Kind));
        Assert.Equal("changed since you added it", rows[1].State);
    }

    [Fact]
    public async Task ListWorksWithoutAConfig()
    {
        var (code, text, _) = await RunAsync("list");

        Assert.Equal(0, code);
        Assert.Single(text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("built in", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListSaysWhatIsWrongWithAPluginInsteadOfFailing()
    {
        var invalid = TestPlugins.Folder(_dir, "broken");
        File.WriteAllText(Path.Combine(invalid, PluginProtocol.ManifestFile), """{ "protocol": 2 }""");
        var missing = _dir.File("gone");
        SaveConfig(new DictumConfig
        {
            Plugins =
            {
                ["broken"] = new PluginRegistration { Path = invalid },
                ["gone"] = new PluginRegistration { Path = missing },
                ["fresh"] = new PluginRegistration { Path = TestPlugins.Folder(_dir, "fresh") },
            },
        });

        var (code, text, _) = await RunAsync("list");

        Assert.Equal(0, code);
        Assert.Contains("broken  your own", text, StringComparison.Ordinal);
        Assert.Contains("plugin.json is not valid: it needs protocol 2", text, StringComparison.Ordinal);
        Assert.Contains($"gone  your own · {missing} · the folder is missing", text, StringComparison.Ordinal);
        Assert.Contains("not confirmed on this computer", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SttListDescribesAPluginProvider()
    {
        var folder = TestPlugins.Folder(_dir);
        SaveConfig(WithProvider(folder));

        var (_, text, _) = await RunAsync("stt", "list");

        Assert.Contains("plugin test · model m1", text, StringComparison.Ordinal);
    }

    private static DictumConfig WithProvider(string folder) => new()
    {
        Stt = { ["p"] = new PluginSttConfig { Plugin = "test", Model = "m1" } },
        Plugins = { ["test"] = new PluginRegistration { Path = folder } },
    };

    private PluginTrust Check(string folder) =>
        _trust.Check("test", folder, PluginManifest.Load(folder).ExecutablePath(folder));

    private void SaveConfig(DictumConfig config) => new ConfigStore(_configFile, NullLog.Instance).Save(config);

    private DictumConfig LoadConfig() => new ConfigStore(_configFile, NullLog.Instance).Load();

    /// <summary>Runs <c>dictum plugins …</c> (or another command) on the test's config; the exit code, stdout and stderr.</summary>
    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        using var console = new ConsoleCapture();
        string[] full = args[0] == "stt" ? args : ["plugins", .. args];
        var code = await console.RunAsync([.. full, "--config", _configFile]);
        return (code, console.Out.ToString(), console.Error.ToString());
    }
}
