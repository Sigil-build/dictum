namespace Dictum.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void DerivedPathsLiveUnderTheirRoots()
    {
        var config = Path.Combine(Path.GetTempPath(), "cfg");
        var data = Path.Combine(Path.GetTempPath(), "data");
        var paths = new AppPaths(config, data);

        Assert.Equal(Path.Combine(config, "config.json"), paths.ConfigFile);
        Assert.Equal(Path.Combine(config, "prompts"), paths.PromptsDirectory);
        Assert.Equal(Path.Combine(data, "models"), paths.ModelsDirectory);
        Assert.Equal(Path.Combine(data, "history.jsonl"), paths.HistoryFile);
    }

    [Fact]
    public void ResolveModelKeepsAbsolutePathsAndMapsNamesToModelsDirectory()
    {
        var paths = new AppPaths(Path.GetTempPath(), Path.GetTempPath());
        var absolute = Path.Combine(Path.GetTempPath(), "custom", "model.bin");

        Assert.Equal(absolute, paths.ResolveModel(absolute));
        Assert.Equal(Path.Combine(paths.ModelsDirectory, "ggml-base.bin"), paths.ResolveModel("ggml-base.bin"));
    }

    [Fact]
    public void DefaultDirectoriesAreAbsolute()
    {
        var paths = AppPaths.FromEnvironment();

        Assert.True(Path.IsPathFullyQualified(paths.ConfigDirectory));
        Assert.True(Path.IsPathFullyQualified(paths.DataDirectory));
        Assert.NotEqual(paths.ConfigDirectory, paths.DataDirectory);
    }
}
