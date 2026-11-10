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

    [Fact]
    public void APluginProviderAndARegistrationRoundTrip()
    {
        var config = DefaultConfig.Create();
        config.Stt["p"] = new PluginSttConfig { Plugin = "my-sherpa", Model = @"D:\m", Threads = 4 };
        config.Plugins["my-sherpa"] = new PluginRegistration { Path = @"D:\plugins\my-sherpa" };

        var json = ConfigStore.Serialize(config);
        var parsed = ConfigStore.Parse(json);

        Assert.Contains("\"type\": \"plugin\"", json);
        Assert.Contains("\"plugins\"", json);
        Assert.Equal(config.Stt["p"], parsed.Stt["p"]);
        Assert.Equal(config.Plugins["my-sherpa"], parsed.Plugins["my-sherpa"]);
    }

    [Fact]
    public void AConfigWithoutPluginsLoadsWithAnEmptySection()
    {
        var parsed = ConfigStore.Parse("""{ "schemaVersion": 1, "stt": {}, "processors": {}, "profiles": [] }""");

        Assert.NotNull(parsed.Plugins);
        Assert.Empty(parsed.Plugins);
    }
}
