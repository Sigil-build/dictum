using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public sealed class PluginIdsTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("0-a", true)]
    [InlineData("whisper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-a", false)]
    [InlineData("Abc", false)]
    [InlineData("a.b", false)]
    public void PluginIdsFollowTheirRule(string? id, bool valid) => Assert.Equal(valid, PluginIds.IsValid(id));

    [Fact]
    public void PluginIdsStopAtFortyCharacters()
    {
        Assert.True(PluginIds.IsValid(new string('a', 40)));
        Assert.False(PluginIds.IsValid(new string('a', 41)));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("parakeet-int8_v1.2", true)]
    [InlineData("whisper", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-a", false)]
    [InlineData(".a", false)]
    [InlineData("..", false)]
    [InlineData("../x", false)]
    [InlineData("Abc", false)]
    [InlineData("My Model", false)]
    [InlineData(" x ", false)]
    public void ModelIdsFollowTheirRule(string? id, bool valid) => Assert.Equal(valid, PluginIds.IsValidModelId(id));

    [Fact]
    public void ModelIdsStopAtSixtyFourCharacters()
    {
        Assert.True(PluginIds.IsValidModelId(new string('a', 64)));
        Assert.False(PluginIds.IsValidModelId(new string('a', 65)));
    }
}
