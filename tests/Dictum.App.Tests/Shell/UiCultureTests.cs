using Dictum.App.Shell;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Shell;

public class UiCultureTests
{
    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("en", "en")]
    [InlineData(null, null)]
    [InlineData("de", null)]
    public void OnlyTheTwoUiLanguagesAreApplied(string? setting, string? culture) =>
        Assert.Equal(culture, UiCulture.Resolve(setting)?.Name);

    [Fact]
    public void TheSettingIsReadFromTheConfig()
    {
        using var dir = new TempDirectory();
        var store = new ConfigStore(dir.File("config.json"), NullLog.Instance);
        Assert.Null(UiCulture.FromConfig(store));

        store.Save(new DictumConfig { General = new GeneralSettings { UiLanguage = "uk" } });
        Assert.Equal("uk", UiCulture.FromConfig(store));

        File.WriteAllText(store.Path, "{ broken");
        Assert.Null(UiCulture.FromConfig(store));
    }
}
