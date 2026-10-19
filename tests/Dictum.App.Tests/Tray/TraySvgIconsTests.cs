using System.Xml.Linq;
using Dictum.App.Tray;

namespace Dictum.App.Tests.Tray;

public class TraySvgIconsTests
{
    [Fact]
    public void EveryStateHasItsOwnFile()
    {
        var names = TraySvgIcons.All().Select(i => i.Name).ToList();

        Assert.Equal(
            [
                "dictum-idle", "dictum-recording", "dictum-recording-mono", "dictum-error", "dictum-error-mono", "dictum-paused",
                "dictum-processing-0", "dictum-processing-1", "dictum-processing-2", "dictum-processing-3", "dictum-processing-4",
            ],
            names);
    }

    [Fact]
    public void ThePanelRecoloursTheMonoParts()
    {
        foreach (var (name, svg) in TraySvgIcons.All())
        {
            var doc = XDocument.Parse(svg);
            XNamespace ns = "http://www.w3.org/2000/svg";
            Assert.Equal("current-color-scheme", doc.Root!.Element(ns + "style")!.Attribute("id")!.Value);
            Assert.Contains(doc.Descendants(), e => (string?)e.Attribute("class") == "ColorScheme-Text");
            Assert.True(svg.Contains("currentColor", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void HostsThatDoNotRecolourDrawAMidGrey()
    {
        Assert.All(TraySvgIcons.All(), icon =>
        {
            Assert.Contains(".ColorScheme-Text { color:#9AA0AE; }", icon.Svg);
            Assert.DoesNotContain("symbolic", icon.Name);
        });
    }

    [Theory]
    [InlineData(TrayGlyph.Recording)]
    [InlineData(TrayGlyph.Error)]
    public void RedIsOnlyInTheColourVariants(TrayGlyph glyph)
    {
        Assert.Contains(TrayGeometry.ColourLayer, TraySvgIcons.Svg(glyph, colour: true));
        Assert.DoesNotContain(TrayGeometry.ColourLayer, TraySvgIcons.Svg(glyph, colour: false));
    }

    [Theory]
    [InlineData(TrayGlyph.Idle)]
    [InlineData(TrayGlyph.Processing)]
    [InlineData(TrayGlyph.Paused)]
    public void MonoStatesHaveNoRedAtAll(TrayGlyph glyph) =>
        Assert.DoesNotContain(TrayGeometry.ColourLayer, TraySvgIcons.Svg(glyph));

    [Fact]
    public void ProcessingFramesStepTheDashesAndLoop()
    {
        var offsets = Enumerable.Range(0, 6).Select(TrayGeometry.DashOffset).ToList();

        Assert.Equal([0, 2.32, 4.64, 6.96, 9.28, 0], offsets.Select(o => Math.Round(o, 2)));
        Assert.Equal(5, Enumerable.Range(0, 5).Select(f => TraySvgIcons.Svg(TrayGlyph.Processing, f)).Distinct().Count());
        Assert.Equal(TraySvgIcons.Name(TrayGlyph.Processing, 0), TraySvgIcons.Name(TrayGlyph.Processing, 5));
    }

    [Fact]
    public void WriteAllPutsOneFilePerIcon()
    {
        using var temp = new TempDirectory();
        var dir = temp.File("icons");

        TraySvgIcons.WriteAll(dir);

        Assert.Equal(11, Directory.GetFiles(dir, "*.svg").Length);
        Assert.Equal(TraySvgIcons.Svg(TrayGlyph.Idle), File.ReadAllText(Path.Combine(dir, "dictum-idle.svg")));
    }
}
