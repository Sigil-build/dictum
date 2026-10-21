using System.Globalization;
using Dictum.Stt.Whisper;

namespace Dictum.Host.Tests.Whisper;

/// <summary>
/// The first language of the profile's list has priority. The cases come from a real log: Ukrainian speech put most of
/// its probability on Russian, which is not in the list, so 'en' came out on top of "uk, en" with p=0.06 and Whisper
/// then decoded Ukrainian speech as English.
/// </summary>
public class WhisperLanguagePriorityTests
{
    private static readonly string[] UkEn = ["uk", "en"];
    private static readonly string[] UkEnPl = ["uk", "en", "pl"];

    [Theory]
    [InlineData("en", 0.06f, "uk")]
    [InlineData("en", 0.00f, "uk")]
    [InlineData("en", 0.90f, "en")]
    [InlineData("uk", 0.07f, "uk")]
    [InlineData("uk", 0.95f, "uk")]
    public void TheFirstLanguageWinsUnlessWhisperIsSureOfAnother(string detected, float probability, string expected) =>
        Assert.Equal(expected, WhisperLocalStt.PickLanguage(UkEn, detected, probability));

    [Theory]
    [InlineData(0.00f)]
    [InlineData(0.50f)]
    [InlineData(0.99f)]
    public void WhenTheModelNamesNoLanguageTheFirstWins(float probability) =>
        Assert.Equal("uk", WhisperLocalStt.PickLanguage(UkEn, null, probability));

    [Fact]
    public void ALanguageOutsideTheListGivesTheFirst() =>
        Assert.Equal("uk", WhisperLocalStt.PickLanguage(UkEn, "ru", 0.80f));

    [Theory]
    [InlineData("pl", 0.60f, "pl")]
    [InlineData("pl", 0.40f, "uk")]
    [InlineData("en", 0.40f, "uk")]
    [InlineData("en", 0.75f, "en")]
    public void InALongerListOnlyTheFirstHasPriority(string detected, float probability, string expected) =>
        Assert.Equal(expected, WhisperLocalStt.PickLanguage(UkEnPl, detected, probability));

    [Fact]
    public void AnotherLanguageNeedsAtLeastHalfTheProbability()
    {
        Assert.Equal(0.5f, WhisperLocalStt.MinOtherLanguageProbability);
        Assert.Equal("en", WhisperLocalStt.PickLanguage(UkEn, "en", WhisperLocalStt.MinOtherLanguageProbability));
        Assert.Equal("uk", WhisperLocalStt.PickLanguage(UkEn, "en", WhisperLocalStt.MinOtherLanguageProbability - 0.01f));
    }

    // The probability is the detected language's, so the debug line puts it next to that language, and it names the
    // priority rule only when the first language won over a detected candidate.
    [Theory]
    [InlineData("en", 0.06f, "'uk' from uk, en (detected 'en', p=0.06; the first language has priority, and another one needs p >= 0.50)")]
    [InlineData("en", 0.90f, "'en' from uk, en (detected 'en', p=0.90)")]
    [InlineData("uk", 0.07f, "'uk' from uk, en (detected 'uk', p=0.07)")]
    [InlineData("ru", 0.80f, "'uk' from uk, en (detected 'ru', p=0.80)")]
    [InlineData(null, 0.00f, "'uk' from uk, en (no language detected)")]
    public void TheDebugLineGivesTheDetectedLanguagesProbabilityAndSaysWhenThePriorityOverrodeIt(
        string? detected, float probability, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.Equal(expected, WhisperLocalStt.DescribePick(UkEn, detected, probability));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // The log line reads the same on every system: a Ukrainian locale writes 0,06, the docs and the threshold say 0.06.
    [Fact]
    public void TheDebugLineWritesProbabilitiesWithADotInAnyCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");

            Assert.Equal(
                "'uk' from uk, en (detected 'en', p=0.06; the first language has priority, and another one needs p >= 0.50)",
                WhisperLocalStt.DescribePick(UkEn, "en", 0.06f));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
