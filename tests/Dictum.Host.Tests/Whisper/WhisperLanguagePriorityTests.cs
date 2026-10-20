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
}
