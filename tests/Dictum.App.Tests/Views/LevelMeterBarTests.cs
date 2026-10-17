using Avalonia;
using Avalonia.Automation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Dictum.App.Controls;
using Dictum.App.Resources;

namespace Dictum.App.Tests.Views;

public class LevelMeterBarTests(AvaloniaFixture fx)
{
    // 30 segments of 7 px with 3 px gaps: segment i spans x = 10·i … 10·i + 6.
    private const int Width = 297;

    private static byte[] Draw(double level)
    {
        var bar = new LevelMeterBar { Level = level };
        bar.Measure(new Size(Width, 10));
        bar.Arrange(new Rect(0, 0, Width, 10));
        using var bitmap = new RenderTargetBitmap(new PixelSize(Width, 10));
        bitmap.Render(bar);
        return Pixels.Of(bitmap);
    }

    private static (byte R, byte G, byte B) Segment(byte[] pixels, int index)
    {
        var (b, g, r, _) = Pixels.At(pixels, Width, (10 * index) + 3, 5);
        return (r, g, b);
    }

    private static (byte R, byte G, byte B) Token(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush), key);
        var colour = ((ISolidColorBrush)brush!).Color;
        return (colour.R, colour.G, colour.B);
    }

    [Fact]
    public Task SegmentsLightUpToTheLevel() => fx.Ui(() =>
    {
        var pixels = Draw(0.58);

        Assert.Equal(Token("color.meter.good"), Segment(pixels, 16));
        Assert.Equal(Token("color.border"), Segment(pixels, 17));
    });

    [Fact]
    public Task AFullLevelEndsInAmberAndRed() => fx.Ui(() =>
    {
        var pixels = Draw(1);

        Assert.Equal(Token("color.meter.good"), Segment(pixels, 22));
        Assert.Equal(Token("color.meter.hot"), Segment(pixels, 23));
        Assert.Equal(Token("color.meter.hot"), Segment(pixels, 26));
        Assert.Equal(Token("color.meter.clip"), Segment(pixels, 27));
        Assert.Equal(Token("color.meter.clip"), Segment(pixels, 29));
    });

    [Fact]
    public Task TheBarIsNamedForScreenReaders() => fx.Ui(() =>
        Assert.Equal(Strings.SettingsInputLevel, AutomationProperties.GetName(new LevelMeterBar())));
}
