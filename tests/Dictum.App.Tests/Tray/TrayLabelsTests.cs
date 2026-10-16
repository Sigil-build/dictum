using Dictum.App.Tray;

namespace Dictum.App.Tests.Tray;

public class TrayLabelsTests
{
    [Theory]
    [InlineData("dictate_uk", "dictate__uk")]
    [InlineData("plain", "plain")]
    [InlineData("a__b", "a____b")]
    public void UnderscoresAreKeptLiteral(string label, string escaped) => Assert.Equal(escaped, TrayLabels.Escape(label));

    [Fact]
    public void AProfileShowsItsLanguages()
    {
        Assert.Equal("dictate__uk (uk, en)", TrayLabels.ForItem(TrayMenuItem.ForProfile("dictate_uk", true, "uk, en")));
        Assert.Equal("Settings", TrayLabels.ForItem(TrayMenuItem.Action("Settings", TrayCommand.Settings)));
    }
}
