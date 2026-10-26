using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public sealed class DictumConfigTests
{
    [Fact]
    public void FindProfileFindsTheIdAsWritten()
    {
        var config = new DictumConfig
        {
            Profiles =
            {
                new ProfileConfig { Id = "work", Stt = "local" },
                new ProfileConfig { Id = "home", Stt = "local" },
            },
        };

        Assert.Same(config.Profiles[1], config.FindProfile("home"));
        Assert.Null(config.FindProfile("Home"));
        Assert.Null(config.FindProfile(null));
    }

    [Fact]
    public void DownloadsFolderRoundTrips()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dictum-downloads");
        var config = DefaultConfig.Create() with { General = new GeneralSettings { DownloadsDirectory = folder } };

        var json = ConfigStore.Serialize(config);

        Assert.Contains("\"downloadsDirectory\"", json);
        Assert.Equal(folder, ConfigStore.Parse(json).General.DownloadsDirectory);
        Assert.DoesNotContain("downloadsDirectory", ConfigStore.Serialize(DefaultConfig.Create()));
    }
}
