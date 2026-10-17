using Dictum.App.Settings;

namespace Dictum.App.Tests.Settings;

public sealed class WordListTests
{
    // Review focus 4.
    [Fact]
    public void PastedListsSplitAndSkipRepeats()
    {
        var (words, rest) = WordList.Split("Nexus5\r\nAvalonia, запуш;; main,\n KDE ,kde\n");

        Assert.Equal(["Nexus5", "Avalonia", "запуш", "main", "KDE", "kde"], words);
        Assert.Equal("", rest);
        Assert.Equal(["Wayland", "Nexus5", "Avalonia", "запуш", "main", "KDE"], WordList.Add(["Wayland"], words));
    }

    // Review focus 4.
    [Fact]
    public void ATrailingPartStaysInTheBox()
    {
        var (words, rest) = WordList.Split("main, pull re");

        Assert.Equal(["main"], words);
        Assert.Equal("pull re", rest);
    }

    [Fact]
    public void TextWithoutASeparatorIsStillBeingTyped()
    {
        var (words, rest) = WordList.Split("Kubern");

        Assert.Empty(words);
        Assert.Equal("Kubern", rest);
    }

    [Fact]
    public void AddingKeepsTheListsOrderAndIgnoresCase() =>
        Assert.Equal(["KDE", "Wayland"], WordList.Add(["KDE"], ["kde", "Wayland", "WAYLAND", "  "]));
}
