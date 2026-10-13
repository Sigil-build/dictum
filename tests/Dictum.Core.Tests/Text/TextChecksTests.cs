using Dictum.Core.Text;

namespace Dictum.Core.Tests.Text;

public class TextChecksTests
{
    [Fact]
    public void StripsByteOrderMarkAndZeroWidthCharacters() =>
        Assert.Equal("Привіт, світ.", TextChecks.StripInvisible("﻿Привіт,​ світ.⁠"));

    [Theory]
    [InlineData("Тест того, що я кажу", 5)]
    [InlineData("  one\ttwo\nthree ", 3)]
    [InlineData("", 0)]
    public void CountsWhitespaceSeparatedWords(string text, int expected) =>
        Assert.Equal(expected, TextChecks.WordCount(text));

    [Theory]
    [InlineData("And so, my fellow Americans", Script.Latin)]
    [InlineData("І так, мої співвітчизники", Script.Cyrillic)]
    [InlineData("Так, перевіряю ще раз, як воно працює, бо мабуть я перейшов в інше вікно у Notepad", Script.Cyrillic)]
    [InlineData("12:30, 15:45!", Script.None)]
    public void FindsTheDominantScript(string text, Script expected) =>
        Assert.Equal(expected, TextChecks.DominantScript(text));

    [Fact]
    public void MixedTextIsNeitherScript()
    {
        // 14 Cyrillic and 10 Latin letters: a fix like "ноутпад" -> "Notepad" must not count as translation.
        Assert.Equal(Script.Mixed, TextChecks.DominantScript("тестую push to talk в ноутпад"));
        Assert.False(TextChecks.IsScriptFlip("тестую push to talk в ноутпад", "Тестую Push to Talk у Notepad."));
    }

    [Fact]
    public void TranslationIsAScriptFlip() =>
        Assert.True(TextChecks.IsScriptFlip(
            "And so, my fellow Americans, ask not what your country can do for you",
            "І так, мої співвітчизники, не питайте, що може зробити ваша країна для вас"));

    [Theory]
    [InlineData("line one\nline two", true)]
    [InlineData("carriage\rreturn", true)]
    [InlineData("one line", false)]
    public void DetectsLineBreaks(string text, bool expected) =>
        Assert.Equal(expected, TextChecks.HasLineBreak(text));
}
