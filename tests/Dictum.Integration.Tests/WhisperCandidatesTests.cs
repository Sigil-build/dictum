using Dictum.Stt.Whisper;

namespace Dictum.Integration.Tests;

/// <summary>Candidate checks run before Whisper is loaded, so no model is needed.</summary>
public class WhisperCandidatesTests
{
    private static readonly string[] Supported = ["en", "uk", "de", "cs", "be"];

    [Fact]
    public void AListOfKnownLanguagesPasses() =>
        Assert.Null(WhisperLocalStt.UnknownCandidate(["uk", "en"], Supported));

    [Fact]
    public void ACountryCodeInTheListIsNamedWithTheLanguageCode()
    {
        var message = WhisperLocalStt.UnknownCandidate(["ua", "en"], Supported);

        Assert.Contains("'ua'", message);
        Assert.Contains("'uk'", message);
    }

    [Fact]
    public void AnUnknownCodeWithoutAKnownSlipIsStillNamed() =>
        Assert.Contains("'xx'", WhisperLocalStt.UnknownCandidate(["en", "xx"], Supported));
}
