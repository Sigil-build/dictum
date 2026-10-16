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

    private static Dictionary<string, string> Entries(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value)];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
