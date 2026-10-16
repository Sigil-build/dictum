using Dictum.Core.Stt;

namespace Dictum.Core.Tests.Stt;

public class SttRequestTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("auto", "auto")]
    [InlineData("uk", "uk")]
    public void ASingleSettingIsTheLanguage(string? setting, string? language)
    {
        var request = SttRequest.ForSetting(setting, "hint")!;

        Assert.Equal(language, request.Language);
        Assert.Null(request.Candidates);
        Assert.Equal("hint", request.Prompt);
    }

    [Fact]
    public void AListBecomesTheCandidates()
    {
        var request = SttRequest.ForSetting("uk, en", null)!;

        Assert.Null(request.Language);
        Assert.Equal(["uk", "en"], request.Candidates!);
    }

    [Theory]
    [InlineData("Ukrainian")]
    [InlineData("")]
    [InlineData("uk,")]
    public void AnInvalidSettingGivesNoRequest(string setting) => Assert.Null(SttRequest.ForSetting(setting, null));
}
