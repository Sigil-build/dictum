using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Dictum.App.Resources;

namespace Dictum.App.Tests.Resources;

public partial class StringsTests
{
    [Fact]
    public void UkrainianComesFromTheSatelliteResources() =>
        Assert.Equal("Вийти з Dictum", Strings.ResourceManager.GetString(nameof(Strings.MenuQuit), CultureInfo.GetCultureInfo("uk")));

    [Fact]
    public void EveryStringHasBothLanguages()
    {
        var english = Entries(CultureInfo.InvariantCulture);
        var ukrainian = Entries(CultureInfo.GetCultureInfo("uk"));

        Assert.Equal(english.Keys.Order(), ukrainian.Keys.Order());
    }

    [Fact]
    public void FormatPlaceholdersMatchAcrossLanguages()
    {
        var english = Entries(CultureInfo.InvariantCulture);
        var ukrainian = Entries(CultureInfo.GetCultureInfo("uk"));

        foreach (var (key, text) in english)
        {
            Assert.True(Placeholders(text).SetEquals(Placeholders(ukrainian[key])), $"{key}: \"{text}\" vs \"{ukrainian[key]}\"");
        }
    }

    // A string nothing shows is dead text that translators still keep up.
    [Fact]
    public void EveryStringIsUsed()
    {
        var src = Path.Combine(Repository.Root, "src");
        var source = string.Concat(Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".cs" or ".axaml" && !IsBuildOutput(f))
            .Select(File.ReadAllText));

        var unused = Entries(CultureInfo.InvariantCulture).Keys.Where(key => !Regex.IsMatch(source, $@"\bStrings\.{key}\b")).Order();

        Assert.Empty(unused);
    }

    // The taskbar and Alt+Tab tell the wizard and Settings apart only by their titles.
    [Theory, InlineData("en"), InlineData("uk")]
    public void TheWizardAndSettingsHaveTitlesOfTheirOwn(string language) =>
        Assert.NotEqual(Text(nameof(Strings.SettingsWindowTitle), language), Text(nameof(Strings.SetupWindowTitle), language));

    // The Windows setting that keeps the tray icon in sight goes by one name: the step after the taskbar settings in the
    // System row's path is the setting the wizard's last step names too.
    [Theory, InlineData("en"), InlineData("uk")]
    public void BothTrayHintsNameTheSameWindowsSetting(string language)
    {
        var setting = Text(nameof(Strings.SettingsTrayHiddenWindows), language).Split(" → ")[1];

        Assert.Contains(setting, Text(nameof(Strings.SetupTrayWindows), language), StringComparison.Ordinal);
    }

    private static string Text(string key, string language) => Strings.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(language))!;

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static Dictionary<string, string> Entries(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
