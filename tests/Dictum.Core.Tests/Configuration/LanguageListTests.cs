using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public class LanguageListTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("auto", "")]
    [InlineData("uk", "uk")]
    [InlineData("haw", "haw")]
    [InlineData("uk, en", "uk|en")]
    [InlineData(" uk ,en ", "uk|en")]
    public void ParsesASettingIntoCodes(string? setting, string expected) =>
        Assert.Equal(expected, string.Join('|', LanguageList.Parse(setting)!));

    [Theory]
    [InlineData("UK")]
    [InlineData("ukrainian")]
    [InlineData("uk, uk")]
    [InlineData("uk, auto")]
    [InlineData("uk; en")]
    [InlineData(",")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" auto ")]
    [InlineData(" uk ")]
    [InlineData("uk,")]
    [InlineData(",uk")]
    [InlineData("uk,,en")]
    public void RejectsWhatIsNotASetting(string setting) => Assert.Null(LanguageList.Parse(setting));

    [Theory]
    [InlineData("uk,en", "uk, en")]
    [InlineData("uk", "uk")]
    [InlineData("auto", "auto")]
    public void FormatsTheCanonicalSetting(string setting, string expected) =>
        Assert.Equal(expected, LanguageList.Format(LanguageList.Parse(setting)!));
}
