using Dictum.Core.Text;

namespace Dictum.Core.Tests.Text;

public sealed class TerminalTextTests
{
    [Theory]
    [InlineData("plain", false, "plain")]
    [InlineData("a\u001bb", true, "a\\u001bb")]
    [InlineData("a\rb", true, "a\\u000db")]
    [InlineData("a\u202eb", true, "a\\u202eb")]
    [InlineData("a\u2028b", true, "a\\u2028b")]
    [InlineData("a\udb40\udc01b", true, "a\\U000e0001b")]
    [InlineData("a\ud83d\ude00b", false, "a\ud83d\ude00b")]
    [InlineData(null, false, "")]
    public void HiddenCharactersAreSpeltOutAndTheRestIsShownAsItIs(string? value, bool hidden, string shown)
    {
        Assert.Equal(hidden, TerminalText.HasHidden(value));
        Assert.Equal(shown, TerminalText.Show(value));
    }

    /// <summary>
    /// A surrogate with no partner is no character at all: it is not hidden, and a terminal draws it as the replacement
    /// character, as <see cref="TerminalText.Show"/> does. Built in code, since a theory's data cannot carry one.
    /// </summary>
    [Fact]
    public void ALoneSurrogateIsNotHiddenAndIsShownAsTheReplacementCharacter()
    {
        foreach (var surrogate in new[] { (char)0xd800, (char)0xdc00 })
        {
            var value = "a" + surrogate + "b";

            Assert.False(TerminalText.HasHidden(value));
            Assert.Equal("a" + (char)0xfffd + "b", TerminalText.Show(value));
        }
    }
}
