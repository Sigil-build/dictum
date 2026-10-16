using Avalonia.Media;
using Dictum.App.Tray;

namespace Dictum.App.Tests.Tray;

public class TrayIconRendererTests(AvaloniaFixture fx)
{
    private static readonly Color Mono = Color.Parse(TrayGeometry.MonoOnLight);

    [Fact]
    public Task EveryStateLooksDifferent() => fx.Ui(() =>
    {
        var images = Enum.GetValues<TrayGlyph>().Select(g => Draw(g)).ToList();

        for (var a = 0; a < images.Count; a++)
        {
            for (var b = a + 1; b < images.Count; b++)
            {
                Assert.False(images[a].SequenceEqual(images[b]), $"{(TrayGlyph)a} and {(TrayGlyph)b} draw the same pixels");
            }
        }
    });

    [Fact]
    public Task StatesDifferWithoutColourToo() => fx.Ui(() =>
    {
        var images = Enum.GetValues<TrayGlyph>().Select(g => Draw(g, colour: false)).ToList();

        Assert.Equal(images.Count, images.Select(Convert.ToBase64String).Distinct().Count());
    });

    [Fact]
    public Task TheFiveProcessingFramesDiffer() => fx.Ui(() =>
    {
        var frames = Enumerable.Range(0, TrayGeometry.ProcessingFrames).Select(f => Convert.ToBase64String(Draw(TrayGlyph.Processing, f))).ToList();

        Assert.Equal(TrayGeometry.ProcessingFrames, frames.Distinct().Count());
    });

    [Theory]
    [InlineData(TrayGlyph.Recording)]
    [InlineData(TrayGlyph.Error)]
    public Task RedIsTheColourLayerOnly(TrayGlyph glyph) => fx.Ui(() =>
    {
        Assert.True(Pixels.Red(Draw(glyph)) > 10);
        Assert.Equal(0, Pixels.Red(Draw(glyph, colour: false)));
    });

    [Fact]
    public Task TheRingIsDrawnInTheMonoColour() => fx.Ui(() =>
    {
        var pixels = Draw(TrayGlyph.Idle);

        Assert.Equal((Mono.B, Mono.G, Mono.R, (byte)255), Pixels.At(pixels, 32, 29, 16));
        Assert.Equal(0, Pixels.At(pixels, 32, 0, 0).A);
    });

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public Task EveryPanelSizeIsDrawnAtItsOwnSize(int size) => fx.Ui(() =>
    {
        using var bitmap = TrayIconRenderer.Render(TrayGlyph.Idle, 0, size, Mono, colourLayer: true);

        Assert.Equal((size, size), (bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        Assert.Contains(Pixels.Of(bitmap).Chunk(4), p => p[3] == 255);
    });

    [Fact]
    public void DashesFollowTheSvgLayout()
    {
        var first = TrayGeometry.Dashes(0);
        var second = TrayGeometry.Dashes(1);

        Assert.Equal((0d, 7d), (first[0].From, first[0].To));
        Assert.Equal(11.6, first[1].From, 3);
        Assert.Equal((0d, 4.68), (second[0].From, Math.Round(second[0].To, 2)));
        Assert.All(first.Concat(second), d => Assert.InRange(d.To, d.From, TrayGeometry.RingLength));
    }

    private static byte[] Draw(TrayGlyph glyph, int frame = 0, bool colour = true)
    {
        using var bitmap = TrayIconRenderer.Render(glyph, frame, 32, Mono, colour);
        return Pixels.Of(bitmap);
    }
}
