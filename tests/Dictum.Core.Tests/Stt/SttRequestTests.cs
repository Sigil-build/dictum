using Dictum.Core.Stt;

namespace Dictum.Core.Tests.Stt;

public class SttRequestTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("auto", "auto")]
    [InlineData("uk", "uk")]
    [InlineData(" uk ", "uk")]
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

    [Fact]
    public void AnInvalidSettingGivesNoRequest() => Assert.Null(SttRequest.ForSetting("Ukrainian", null));
}
