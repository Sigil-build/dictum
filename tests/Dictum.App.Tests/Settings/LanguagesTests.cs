using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class LanguagesTests
{
    [Fact]
    public void WhisperKnowsAHundredLanguages()
    {
        Assert.Equal(100, WhisperLanguages.Codes.Count);
        Assert.Equal(100, WhisperLanguages.Codes.Distinct().Count());
        Assert.Contains("uk", WhisperLanguages.Codes);
    }

    [Theory]
    [InlineData("uk", "Українська")]
    [InlineData("en", "English")]
    [InlineData("be", "Беларуская")]
    public void NamesAreTheLanguagesOwnWithACapital(string code, string name) => Assert.Equal(name, WhisperLanguages.Name(code));

    [Fact]
    public void ACodeWithoutANameShowsTheCode() => Assert.Equal("xx", WhisperLanguages.Name("xx"));

    [Theory]
    [InlineData(null, LanguageMode.Auto, "")]
    [InlineData("auto", LanguageMode.Auto, "")]
    [InlineData("uk", LanguageMode.One, "uk")]
    [InlineData("uk, en", LanguageMode.Several, "uk,en")]
    public void TheSettingParses(string? setting, LanguageMode mode, string codes)
    {
        var parsed = LanguageSetting.Parse(setting);

        Assert.Equal(mode, parsed.Mode);
        Assert.Equal(codes, string.Join(',', parsed.Codes));
    }

    [Fact]
    public void SeveralWithOneCodeStaysSeveralWhileShown()
    {
        Assert.Equal(LanguageMode.One, LanguageSetting.Parse("uk").Mode);
        Assert.Equal(LanguageMode.Several, LanguageSetting.Parse("uk", LanguageMode.Several).Mode);
        Assert.Equal(LanguageMode.Several, LanguageSetting.Parse("uk, en", LanguageMode.One).Mode);
    }

    [Fact]
    public void ModesFormatAsTheConfigExpects()
    {
        var several = LanguageSetting.Parse("uk, en");

        Assert.Equal("uk, en", several.Format());
        Assert.Equal("uk", several.WithMode(LanguageMode.One, "en").Format());
        Assert.Equal("auto", several.WithMode(LanguageMode.Auto, "en").Format());
        Assert.Equal("en", LanguageSetting.Parse("auto").WithMode(LanguageMode.One, "en").Format());
        Assert.Equal("en", LanguageSetting.Parse(null).WithMode(LanguageMode.Several, "en").Format());
    }

    [Fact]
    public void AddingAndRemovingFollowTheMode()
    {
        Assert.Equal("pl", LanguageSetting.Parse("uk").With("pl").Format());
        Assert.Equal("uk, en, pl", LanguageSetting.Parse("uk, en").With("pl").Format());
        Assert.Equal("uk, en", LanguageSetting.Parse("uk, en").With("en").Format());
        Assert.Equal("uk", LanguageSetting.Parse("uk, en").Without("en").Format());
        Assert.Equal("uk", LanguageSetting.Parse("uk").Without("uk").Format());
    }

    [Fact]
    public void MakingALanguageMainMovesItToTheFront()
    {
        Assert.Equal("en, uk, pl", LanguageSetting.Parse("uk, en, pl").WithMain("en").Format());
        Assert.Equal("pl, uk, en", LanguageSetting.Parse("uk, en, pl").WithMain("pl").Format());
        Assert.Equal("uk, en", LanguageSetting.Parse("uk, en").WithMain("uk").Format());
        Assert.Equal("uk, en", LanguageSetting.Parse("uk, en").WithMain("pl").Format());
        Assert.Equal(LanguageMode.Several, LanguageSetting.Parse("uk", LanguageMode.Several).WithMain("uk").Mode);
    }
}
