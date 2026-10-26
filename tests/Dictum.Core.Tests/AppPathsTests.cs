using Dictum.Testing;

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
        Assert.Equal(Path.Combine(data, "update-check.json"), paths.UpdateCheckFile);
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
    public void ModelsLiveInTheDataFolderByDefault()
    {
        var data = Path.Combine(Path.GetTempPath(), "dictum-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths("c", data);

        Assert.Equal(Path.Combine(data, "models"), paths.ModelsDirectory);
        Assert.Equal(data, paths.DownloadsDirectory);
        Assert.Null(paths.ConfiguredDownloadsDirectory);
        Assert.False(paths.IsDownloadsDirectoryMissing);
    }

    [Fact]
    public void ModelsFollowTheDownloadsFolder()
    {
        using var downloads = new TempDirectory();
        var config = Path.Combine(Path.GetTempPath(), "cfg");
        var data = Path.Combine(Path.GetTempPath(), "data");

        var paths = new AppPaths(config, data).WithDownloads(downloads.Path);

        Assert.Equal(Path.Combine(downloads.Path, "models"), paths.ModelsDirectory);
        Assert.Equal((config, data), (paths.ConfigDirectory, paths.DataDirectory));
        Assert.Equal(Path.Combine(data, "history.jsonl"), paths.HistoryFile);
        Assert.Equal(Path.Combine(data, "downloads.json"), paths.DownloadsFile);
        Assert.Equal(Path.Combine(data, "setup.json"), paths.SetupFile);
        Assert.False(paths.IsDownloadsDirectoryMissing);
    }

    [Fact]
    public void ALiveFolderIsReadOnEveryUse()
    {
        var folder = Path.Combine(Path.GetTempPath(), "first");
        var paths = new AppPaths("c", "d").WithDownloads(() => folder);

        var before = paths.ModelsDirectory;
        folder = Path.Combine(Path.GetTempPath(), "second");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "first", "models"), before);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "second", "models"), paths.ModelsDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void ABlankFolderMeansTheDefault(string? folder)
    {
        var paths = new AppPaths("c", "d").WithDownloads(folder);

        Assert.Null(paths.ConfiguredDownloadsDirectory);
        Assert.Equal("d", paths.DownloadsDirectory);
    }

    [Fact]
    public void AConfiguredFolderThatIsGoneIsMissing()
    {
        var gone = Path.Combine(Path.GetTempPath(), "dictum-tests", Guid.NewGuid().ToString("N"));

        var paths = new AppPaths("c", "d").WithDownloads(gone);

        Assert.True(paths.IsDownloadsDirectoryMissing);
        Assert.Equal(Path.Combine(gone, "models"), paths.ModelsDirectory);
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
