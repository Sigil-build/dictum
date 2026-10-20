using System.Globalization;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Tests.Setup;

public sealed class SetupLanguagesStepTests
{
    private static LanguageRow Row(SetupLanguagesStep step, string code) => step.Rows.Single(r => r.Code == code);

    private static IEnumerable<string> MainOne(SetupLanguagesStep step) => step.Rows.Where(r => r.IsMain).Select(r => r.Code);

    private static IEnumerable<string> OfferingMain(SetupLanguagesStep step) => step.Rows.Where(r => r.OffersMain).Select(r => r.Code);

    /// <summary>A step made, and used, under another UI language.</summary>
    private static void WithUi(string culture, Action test)
    {
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            test();
        }
        finally
        {
            CultureInfo.CurrentUICulture = ui;
        }
    }

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

    // Whisper takes another language of the list only when it is sure of it, so the step shows which one comes first.
    [Fact]
    public void TheSettingsFirstLanguageIsTheMainOne()
    {
        var step = new SetupLanguagesStep("uk, en");

        Assert.Equal(["uk"], MainOne(step));
        Assert.Equal(["en"], OfferingMain(step));
    }

    [Fact]
    public void MakeMainPutsAPickedLanguageFirst()
    {
        var step = new SetupLanguagesStep("uk, en");
        Row(step, "pl").IsChecked = true;

        Row(step, "pl").MakeMainCommand.Execute(null);

        Assert.Equal("pl, uk, en", step.Setting);
        Assert.Equal(["pl"], MainOne(step));
        Assert.Equal(["uk", "en"], OfferingMain(step));
    }

    // A first setup on an English system ticks English; Ukrainian ticked after it is not main until the user says so.
    [Fact]
    public void AFirstSetupOnAnEnglishSystemShowsEnglishAsMainUntilTheUserChooses() => WithUi("en", () =>
    {
        var step = new SetupLanguagesStep("en", fresh: true);

        Row(step, "uk").IsChecked = true;
        Assert.Equal("en, uk", step.Setting);
        Assert.Equal(["en"], MainOne(step));
        Assert.Equal(["uk"], OfferingMain(step));

        Row(step, "uk").MakeMainCommand.Execute(null);
        Assert.Equal("uk, en", step.Setting);
        Assert.Equal(["uk"], MainOne(step));
        Assert.Equal(["en"], OfferingMain(step));
    });

    // On a Ukrainian system the UI language is the main one by default, even when it is ticked again after English.
    [Fact]
    public void AFirstSetupMakesTheUiLanguageMainWhilePicked() => WithUi("uk", () =>
    {
        var step = new SetupLanguagesStep("uk, en", fresh: true);
        Assert.Equal(["uk"], MainOne(step));

        Row(step, "uk").IsChecked = false;
        Assert.Equal("en", step.Setting);
        Assert.Equal(["en"], MainOne(step));

        Row(step, "uk").IsChecked = true;
        Assert.Equal("uk, en", step.Setting);
        Assert.Equal(["uk"], MainOne(step));
    });

    // A Russian system ticks Russian and English; with Russian unticked, English, picked first, stays the main one.
    [Fact]
    public void WithoutTheUiLanguageTheFirstPickedIsMain() => WithUi("ru", () =>
    {
        var step = new SetupLanguagesStep("ru, en", fresh: true);

        Row(step, "ru").IsChecked = false;
        Row(step, "uk").IsChecked = true;

        Assert.Equal("en, uk", step.Setting);
        Assert.Equal(["en"], MainOne(step));
    });

    [Fact]
    public void TheUsersChoiceOutranksTheUiLanguage() => WithUi("uk", () =>
    {
        var step = new SetupLanguagesStep("uk, en", fresh: true);

        Row(step, "en").MakeMainCommand.Execute(null);
        Row(step, "pl").IsChecked = true;

        Assert.Equal("en, uk, pl", step.Setting);
    });

    // A config's or a kept walk's list is the user's own: its first language stays main whatever the UI language.
    [Fact]
    public void AnExistingSettingKeepsItsMainLanguage() => WithUi("uk", () =>
    {
        var step = new SetupLanguagesStep("en, uk");

        Assert.Equal("en, uk", step.Setting);
        Assert.Equal(["en"], MainOne(step));
    });

    [Fact]
    public void DetectAnyHasNoMainLanguage()
    {
        var step = new SetupLanguagesStep("uk, en") { DetectAny = true };

        Assert.Empty(MainOne(step));
        Assert.Empty(OfferingMain(step));
    }

    // People search in the language they read, not only by each language's own name.
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
