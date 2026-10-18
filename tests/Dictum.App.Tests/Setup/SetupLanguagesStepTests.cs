using Dictum.App.Settings;
using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupLanguagesStepTests
{
    private static LanguageRow Row(SetupLanguagesStep step, string code) => step.Rows.Single(r => r.Code == code);

    [Fact]
    public void ThePickedLanguagesComeFirstInTheirOrder()
    {
        var step = new SetupLanguagesStep("uk, en");

        Assert.Equal("Languages", step.Name);
        Assert.Equal(WhisperLanguages.Codes.Count, step.Rows.Count);
        Assert.Equal(["uk", "en"], step.Rows.Take(2).Select(r => r.Code));
        Assert.All(step.Rows.Take(2), r => Assert.True(r.IsChecked));
        Assert.Equal(("uk, en", true, false), (step.Setting, step.CanContinue, step.DetectAny));
    }

    [Fact]
    public void TickingAndUntickingKeepsThePickedOrder()
    {
        var step = new SetupLanguagesStep("uk, en");

        Row(step, "pl").IsChecked = true;
        Assert.Equal("uk, en, pl", step.Setting);

        Row(step, "uk").IsChecked = false;
        Assert.Equal("en, pl", step.Setting);
    }

    [Fact]
    public void NextNeedsALanguageOrDetectAny()
    {
        var step = new SetupLanguagesStep("en");

        Row(step, "en").IsChecked = false;
        Assert.False(step.CanContinue);

        step.DetectAny = true;
        Assert.Equal((true, "auto"), (step.CanContinue, step.Setting));
    }

    [Fact]
    public void AutoStartsWithDetectAny()
    {
        var step = new SetupLanguagesStep("auto");

        Assert.True(step.DetectAny);
        Assert.DoesNotContain(step.Rows, r => r.IsChecked);
        Assert.Equal("auto", step.Setting);
    }

    [Fact]
    public void TheSearchFindsNamesAndCodesAndKeepsPickedRows()
    {
        var step = new SetupLanguagesStep("uk, en");

        step.Search = "pol";
        Assert.Equal(["uk", "en", "pl"], step.Rows.Where(r => r.IsShown).Select(r => r.Code));

        step.Search = "BE";
        Assert.Contains("be", step.Rows.Where(r => r.IsShown).Select(r => r.Code));

        step.Search = "";
        Assert.All(step.Rows, r => Assert.True(r.IsShown));
    }

    [Fact]
    public void SimilarLanguagesRaiseTheBanner()
    {
        var step = new SetupLanguagesStep("uk, en");
        Assert.Null(step.BannerLead);

        Row(step, "be").IsChecked = true;
        Assert.Equal("Українська and Беларуская are closely related.", step.BannerLead);

        step.DetectAny = true;
        Assert.Null(step.BannerLead);
    }

    // Final review I3: people search in the language they read, not only by each language's own name.
    [Theory]
    [InlineData("ukrainian", "uk")]
    [InlineData("German", "de")]
    [InlineData("japan", "ja")]
    public void TheSearchFindsEnglishNamesToo(string search, string code)
    {
        var step = new SetupLanguagesStep("en") { Search = search };

        Assert.Contains(code, step.Rows.Where(r => r.IsShown).Select(r => r.Code));
    }
}
