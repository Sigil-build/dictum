using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Settings;

public sealed class DownloadsLocationTests
{
    private static readonly string Configured = Path.Combine(Path.GetTempPath(), "configured");
    private static readonly string Chosen = Path.Combine(Path.GetTempPath(), "chosen");

    private static DictumConfig ConfigWith(string? folder) => new() { General = new GeneralSettings { DownloadsDirectory = folder } };

    // The wizard downloads before it saves the config: until then its choice is where the files go.
    [Fact]
    public void PendingWinsUntilCleared()
    {
        var location = new DownloadsLocation();
        location.OnConfig(ConfigWith(Configured));

        location.Choose(Chosen);

        Assert.Equal((Configured, Chosen, Chosen), (location.Configured, location.Pending, location.Current));

        location.Choose(null);

        Assert.Equal(Configured, location.Current);
    }

    [Fact]
    public void OnConfigUpdatesConfiguredAndRaisesChangedOnlyOnAChange()
    {
        var location = new DownloadsLocation();
        var changes = 0;
        location.Changed += () => changes++;

        location.OnConfig(ConfigWith(Configured));
        location.OnConfig(ConfigWith(Configured));
        location.OnConfig(new DictumConfig());
        location.OnConfig(null);

        Assert.Equal(2, changes);
        Assert.Null(location.Current);
    }

    [Fact]
    public void UseSetsTheConfiguredFolder()
    {
        var location = new DownloadsLocation();
        var changes = 0;
        location.Changed += () => changes++;

        location.Use(Chosen);

        Assert.Equal((Chosen, Chosen, 1), (location.Configured, location.Current, changes));
    }

    [Fact]
    public void RestorePrefersTheKeptWizardChoice()
    {
        var location = new DownloadsLocation();

        location.Restore(ConfigWith(Configured), Chosen);

        Assert.Equal((Configured, Chosen), (location.Configured, location.Current));

        location.Restore(ConfigWith(Configured), null);

        Assert.Equal(Configured, location.Current);
    }
}
