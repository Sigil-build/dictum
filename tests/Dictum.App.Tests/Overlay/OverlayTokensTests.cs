using Dictum.App.Overlay;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Overlay;

public class OverlayTokensTests
{
    [Theory]
    [InlineData(OverlaySize.Small, 0.8)]
    [InlineData(OverlaySize.Medium, 1.0)]
    [InlineData(OverlaySize.Large, 1.25)]
    public void SizesScaleTheOverlay(OverlaySize size, double scale) => Assert.Equal(scale, OverlayTokens.SizeScale(size));

    [Fact]
    public void ThePalettesMatchTheDesign()
    {
        Assert.Equal(0xFF7A6BFFu, OverlayTokens.Palette(OverlayPalette.Aurora).Wave2);
        Assert.Equal(0xFFFF5E8Au, OverlayTokens.Palette(OverlayPalette.Ember).Cleaning1);
        Assert.Equal(0.10, OverlayTokens.Palette(OverlayPalette.Mist).GlowOpacity);
    }

    [Fact]
    public void TheWavesCoverEveryBandAndOverlap()
    {
        var waves = OverlayTokens.Waves;

        Assert.Equal(5, waves.Count);
        Assert.Equal(0, waves[0].FirstBand);
        Assert.Equal(23, waves[^1].LastBand);
        for (var i = 1; i < waves.Count; i++)
        {
            Assert.True(waves[i].FirstBand <= waves[i - 1].LastBand, $"wave {i + 1} leaves a gap");
        }
    }
}
