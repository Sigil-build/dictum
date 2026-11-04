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

    /// <summary>A plugin's own lines reach a terminal through doctor's row and Settings' Test, so they are spelt out.</summary>
    [Fact]
    public void TheLastLinesAreShownEscaped()
    {
        var text = new PluginUnavailableException("m", null, ["\u001b[31mboom", "a\u202eb"]).WithOutput("It stopped.");

        Assert.Equal("It stopped; last output: \\u001b[31mboom | a\\u202eb", text);
    }

    [Fact]
    public void WithoutOutputTheTextIsAsItWas() =>
        Assert.Equal("It stopped...", new PluginUnavailableException("m").WithOutput("It stopped..."));
}
