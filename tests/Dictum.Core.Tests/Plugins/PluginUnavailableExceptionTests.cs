using Dictum.Plugins;

namespace Dictum.Core.Tests.Plugins;

public sealed class PluginUnavailableExceptionTests
{
    [Theory]
    [InlineData("It stopped.", "It stopped; last output: a | b")]
    [InlineData("It stopped...", "It stopped..; last output: a | b")]
    [InlineData("It stopped", "It stopped; last output: a | b")]
    public void TheLastLinesFollowTheTextWithoutItsFinalPeriodOnly(string text, string expected) =>
        Assert.Equal(expected, new PluginUnavailableException("m", null, ["a", "b"]).WithOutput(text));

    [Fact]
    public void WithoutOutputTheTextIsAsItWas() =>
        Assert.Equal("It stopped...", new PluginUnavailableException("m").WithOutput("It stopped..."));
}
