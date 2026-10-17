using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Dictum.App.Overlay;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Overlay;

public class OverlayCanvasTests(AvaloniaFixture fx)
{
    private const int W = 480;

    private static OverlayScene Scene(
        OverlayPhase phase, double since = 0.05, double level = 0.6, bool quiet = false, string? chip = null,
        string? message = null, bool reduced = false, OverlayPalette palette = OverlayPalette.Aurora) =>
        new(phase, since, 1.0, 1, level, quiet, [0.5, 0.4, 0.3, 0.2, 0.1], chip, message, reduced, palette);

    /// <summary>Draws the canvas inside a parent, as the window holds it, into 480 × 96 BGRA pixels.</summary>
    private static byte[] Draw(OverlayScene scene, double opacity = 1)
    {
        var canvas = new OverlayCanvas { Scene = scene, LayerOpacity = opacity };
        var host = new Decorator { Child = canvas };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(host.DesiredSize));
        using var bitmap = new RenderTargetBitmap(new PixelSize(W, 96));
        bitmap.Render(host);
        return Pixels.Of(bitmap);
    }

    private static int Count(byte[] pixels, Func<(byte B, byte G, byte R, byte A), bool> match, int x0 = 0, int y0 = 0, int x1 = W - 1, int y1 = 95)
    {
        var count = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (match(Pixels.At(pixels, W, x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool Any((byte B, byte G, byte R, byte A) p) => p.A > 0;

    private static bool Light((byte B, byte G, byte R, byte A) p) => p.A > 120 && p.R > 120 && p.G > 120 && p.B > 120;

    [Fact]
    public Task HiddenDrawsNothing() => fx.Ui(() => Assert.Equal(0, Count(Draw(OverlayScene.Hidden), Any)));

    [Theory]
    [InlineData(OverlayPhase.Listening, 0.05, false)]
    [InlineData(OverlayPhase.Transcribing, 0.5, false)]
    [InlineData(OverlayPhase.CleaningUp, 0.5, false)]
    [InlineData(OverlayPhase.Done, 0.1, false)]
    [InlineData(OverlayPhase.Cancelled, 0.05, false)]
    [InlineData(OverlayPhase.Error, 0.5, false)]
    [InlineData(OverlayPhase.Listening, 0.05, true)]
    [InlineData(OverlayPhase.Transcribing, 0.5, true)]
    [InlineData(OverlayPhase.CleaningUp, 0.5, true)]
    [InlineData(OverlayPhase.Done, 0.1, true)]
    [InlineData(OverlayPhase.Cancelled, 0.05, true)]
    [InlineData(OverlayPhase.Error, 0.5, true)]
    public Task EveryStateDrawsSomething(OverlayPhase phase, double since, bool reduced) => fx.Ui(() =>
        Assert.True(Count(Draw(Scene(phase, since, message: "Microphone not found", reduced: reduced)), p => p.A > 40) > 100));

    [Fact]
    public Task TheCornersStayClear() => fx.Ui(() =>
    {
        var pixels = Draw(Scene(OverlayPhase.Listening, level: 1));

        Assert.Equal(0, Count(pixels, Any, 0, 0, 20, 20));
        Assert.Equal(0, Count(pixels, Any, W - 21, 0, W - 1, 20));
    });

    [Fact]
    public Task TheWavesUseThePaletteColours() => fx.Ui(() =>
    {
        static bool Strong((byte B, byte G, byte R, byte A) p) => p.A > 150;

        var aurora = Draw(Scene(OverlayPhase.Listening));
        Assert.True(Count(aurora, p => Strong(p) && p.B > p.G + 60, 200, 0, 280, 95) > 10, "Aurora is blue-violet in the middle");

        var mist = Draw(Scene(OverlayPhase.Listening, palette: OverlayPalette.Mist));
        Assert.True(Count(mist, Strong, 200, 0, 280, 95) > 10);
        Assert.Equal(0, Count(mist, p => Strong(p) && Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) > 50));
    });

    [Fact]
    public Task DoneDrawsALightCheck() => fx.Ui(() =>
        Assert.True(Count(Draw(Scene(OverlayPhase.Done, since: 0.2)), Light, 236, 36, 250, 52) > 3));

    [Fact]
    public Task AnErrorShowsARedLineAndItsMessage() => fx.Ui(() =>
    {
        var pixels = Draw(Scene(OverlayPhase.Error, since: 0.5, message: "Microphone not found"));

        Assert.True(Count(pixels, p => p.A > 150 && p.R > 180 && p.G < 140 && p.B < 130, 0, 40, W - 1, 48) > 200, "red line");
        Assert.True(Count(pixels, Light, 182, 58, 330, 78) > 20, "message");
    });

    // Review focus 1: a provider's long HTTP error.
    [Fact]
    public Task ALongErrorStaysOnOneLineInsideTheOverlay() => fx.Ui(() =>
    {
        var message = string.Concat(Enumerable.Repeat("The speech server answered 503 Service Unavailable. ", 6));

        var pixels = Draw(Scene(OverlayPhase.Error, since: 0.5, message: message));

        Assert.Equal(0, Count(pixels, Any, 0, 80, W - 1, 95));
        Assert.Equal(0, Count(pixels, Any, W - 12, 56, W - 1, 80));
        Assert.True(Count(pixels, Light, 182, 58, W - 16, 78) > 100);
    });

    // Review focus 1, final review I1: providers answer with pretty-printed JSON, so the message has line breaks.
    [Fact]
    public Task AMultiLineErrorIsShownOnOneLine() => fx.Ui(() =>
    {
        var message = """
            LLM 'clean' answered 401: {
                "error": {
                    "message": "Incorrect API key provided: sk-proj-abc",
                    "type": "invalid_request_error"
                }
            }
            """;

        var pixels = Draw(Scene(OverlayPhase.Error, since: 0.5, message: message));

        Assert.True(Count(pixels, Light, 400, 58, W - 16, 78) > 20, "the text runs on to the ellipsis");
        Assert.Equal(0, Count(pixels, Any, 0, 80, W - 1, 95));
    });

    [Fact]
    public Task ReducedMotionDrawsALevelBarInsteadOfWaves() => fx.Ui(() =>
    {
        var pixels = Draw(Scene(OverlayPhase.Listening, level: 0.5, reduced: true));

        var filled = Pixels.At(pixels, W, 150, 44);
        var track = Pixels.At(pixels, W, 330, 44);
        Assert.True(filled.A > 200, $"filled part {filled}");
        Assert.InRange(track.A, 50, 140);
        Assert.Equal(0, Count(pixels, Any, 120, 0, 360, 30));
    });

    [Fact]
    public Task TheChipShowsTheLanguages() => fx.Ui(() =>
    {
        var with = Draw(Scene(OverlayPhase.Listening, level: 0, chip: "UK/EN"));
        var without = Draw(Scene(OverlayPhase.Listening, level: 0));

        // The chip sits on the centre line, where the waves already draw: count what it changes.
        int Changed(int x0, int x1)
        {
            var count = 0;
            for (var y = 30; y <= 58; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    count += Pixels.At(with, W, x, y) == Pixels.At(without, W, x, y) ? 0 : 1;
                }
            }

            return count;
        }

        Assert.True(Changed(380, 463) > 300, $"{Changed(380, 463)} pixels changed");
        Assert.Equal(0, Changed(466, W - 1));
    });

    [Fact]
    public Task OpacityAppliesToTheWholeDrawing() => fx.Ui(() =>
    {
        var pixels = Draw(Scene(OverlayPhase.Error, since: 0.5, message: "x"), opacity: 0.5);

        var strongest = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            strongest = Math.Max(strongest, pixels[i]);
        }

        Assert.InRange(strongest, 1, 140);
    });

    [Fact]
    public Task OverlappingWavesFadeAsOneLayer() => fx.Ui(() =>
    {
        static int Strongest(byte[] pixels)
        {
            var strongest = 0;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                strongest = Math.Max(strongest, pixels[i]);
            }

            return strongest;
        }

        var full = Strongest(Draw(Scene(OverlayPhase.Listening)));
        var half = Strongest(Draw(Scene(OverlayPhase.Listening), opacity: 0.5));

        // Per-stroke opacity would let the five overlapping waves add up to far more than half.
        Assert.InRange(half, 1, full / 2 + 3);
    });

    [Fact]
    public Task TheSizeSettingScalesTheCanvas() => fx.Ui(() =>
    {
        var canvas = new OverlayCanvas { Zoom = 1.25 };

        canvas.Measure(Size.Infinity);

        Assert.Equal(new Size(600, 120), canvas.DesiredSize);
    });
}
