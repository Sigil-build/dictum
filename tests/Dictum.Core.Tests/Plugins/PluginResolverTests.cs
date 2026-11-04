using Dictum.Core.Configuration;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Core.Tests.Plugins;

/// <summary>
/// The resolver over a plugin folder with a hand-written <c>plugin.json</c> and a dummy program, which never runs here.
/// The folder's name has a space, so the hints that name it must quote it.
/// </summary>
public sealed class PluginResolverTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDirectory _dir = new();
    private readonly AppPaths _paths;
    private readonly string _folder;

    public PluginResolverTests()
    {
        _paths = new AppPaths(_dir.File("config"), _dir.File("data"));
        _folder = _dir.File("test plugin");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void AnUnaddedPluginSaysHowToAddIt()
    {
        var resolver = new PluginResolver(_paths, new Dictionary<string, PluginRegistration>());

        var error = Assert.Throws<PluginUnavailableException>(() => resolver.Resolve(Config(ExistingModel())));

        Assert.Equal("Plugin 'test' is not added.", error.Message);
        Assert.Equal("Add it with 'dictum plugins add <folder>'.", error.Hint);
    }

    [Fact]
    public void ABrokenManifestSaysHowToFixIt()
    {
        File.WriteAllText(Path.Combine(_folder, PluginProtocol.ManifestFile), "{ not json");
        var manifestError = Assert.Throws<PluginManifestException>(() => PluginManifest.Load(_folder));

        var error = Assert.Throws<PluginUnavailableException>(() => Resolver().Resolve(Config(ExistingModel())));

        Assert.Equal(manifestError.Message, error.Message);
        Assert.Equal($"Fix its plugin.json, then confirm it with 'dictum plugins add \"{_folder}\"'.", error.Hint);
    }

    [Fact]
    public void AManifestWithAnotherIdIsRefused()
    {
        WriteManifest(id: "other");

        var error = Assert.Throws<PluginUnavailableException>(() => Resolver().Resolve(Config(ExistingModel())));

        Assert.Equal($"The plugin in {_folder} calls itself 'other', not 'test'.", error.Message);
        Assert.Equal($"Add it again with 'dictum plugins add \"{_folder}\"'.", error.Hint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAbsoluteModelIsUsedAsIs(bool isFolder)
    {
        WriteManifest();
        var model = ExistingModel(isFolder);

        var launch = Resolver().Resolve(Config(model));

        Assert.Equal(model, launch.Model);
    }

    [Fact]
    public void AMissingAbsoluteModelIsReported()
    {
        WriteManifest();
        var model = _dir.File("gone.bin");

        var error = Assert.Throws<PluginUnavailableException>(() => Resolver().Resolve(Config(model)));

        Assert.Equal($"Model not found: {model}", error.Message);
        Assert.Equal("Check stt.<id>.model in config.json.", error.Hint);
    }

    [Fact]
    public void AManifestModelResolvesUnderTheModelsFolder()
    {
        WriteManifest(models: ["m1"]);
        var folder = Path.Combine(_paths.ModelsDirectory, "test", "m1");
        Directory.CreateDirectory(folder);

        var launch = Resolver().Resolve(Config("m1"));

        Assert.Equal(folder, launch.Model);
    }

    [Fact]
    public void AMissingManifestModelSaysWhereItShouldBe()
    {
        WriteManifest(models: ["m1"]);

        var error = Assert.Throws<PluginUnavailableException>(() => Resolver().Resolve(Config("m1")));

        Assert.Equal($"Model 'm1' of plugin 'test' is not in {Path.Combine(_paths.ModelsDirectory, "test", "m1")}.", error.Message);
        Assert.Equal("Put its files there, or set the model to an absolute path.", error.Hint);
    }

    [Fact]
    public void AnUnknownModelListsThePluginsModels()
    {
        WriteManifest(models: ["m1", "m2"]);
        var error = Assert.Throws<PluginUnavailableException>(() => Resolver().Resolve(Config("m3")));
        WriteManifest();
        var none = Assert.Throws<PluginUnavailableException>(() => Resolver().Resolve(Config("m3")));

        Assert.Equal("Plugin 'test' has no model 'm3'.", error.Message);
        Assert.Equal("Its models: m1, m2; or set the model to an absolute path.", error.Hint);
        Assert.Equal("Plugin 'test' has no model 'm3'.", none.Message);
        Assert.Equal("It lists no models; set the model to an absolute path.", none.Hint);
    }

    [Fact]
    public void ThreadsAndVersionReachTheLaunch()
    {
        WriteManifest(version: "1.2.0");
        var model = ExistingModel();

        var launch = Resolver().Resolve(Config(model) with { Threads = 3 });

        Assert.Equal(
            new PluginLaunch("test", "Test plugin", "1.2.0", _folder, Path.Combine(_folder, "plugin.exe"), model, 3, Path.Combine(_paths.DataDirectory, "plugin-cache", "test")),
            launch);
    }

    /// <summary>The plugin host tells processes apart by the folder's spelling, so one folder must always be spelt alike.</summary>
    [Fact]
    public void TheFolderAndModelAreSpeltOneWay()
    {
        WriteManifest();
        var model = ExistingModel();
        var registered = Path.Combine(_folder, "..", Path.GetFileName(_folder)) + Path.DirectorySeparatorChar;
        var resolver = new PluginResolver(_paths, new Dictionary<string, PluginRegistration> { ["test"] = new() { Path = registered } });
        var spelt = Path.Combine(_dir.Path, ".", Path.GetFileName(model));

        var launch = resolver.Resolve(Config(spelt));

        Assert.Equal(_folder, launch.Folder);
        Assert.Equal(Path.Combine(_folder, "plugin.exe"), launch.Executable);
        Assert.Equal(model, launch.Model);
    }

    private PluginResolver Resolver() =>
        new(_paths, new Dictionary<string, PluginRegistration> { ["test"] = new() { Path = _folder } });

    private static PluginSttConfig Config(string model) => new() { Plugin = "test", Model = model };

    /// <summary>A model file, or folder, outside the models folder, as an absolute model path names one.</summary>
    private string ExistingModel(bool isFolder = false)
    {
        var path = _dir.File(isFolder ? "model-folder" : "model.bin");
        if (isFolder)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, "weights");
        }

        return path;
    }

    private void WriteManifest(string id = "test", string version = "1.0.0", string[]? models = null)
    {
        File.WriteAllText(Path.Combine(_folder, "plugin.exe"), "program");
        var entries = (models ?? []).Select(m =>
            $$"""{"id":"{{m}}","title":"{{m}}","files":[{"file":"a.bin","url":"https://example.com/a.bin","sha256":"{{Sha}}","size":1}],"languages":["en"]}""");
        File.WriteAllText(
            Path.Combine(_folder, PluginProtocol.ManifestFile),
            $$"""
            {
              "protocol": 1,
              "id": "{{id}}",
              "name": "Test plugin",
              "version": "{{version}}",
              "executable": { "{{PluginProtocol.CurrentRid}}": "plugin.exe" },
              "models": [{{string.Join(",", entries)}}]
            }
            """);
    }
}
