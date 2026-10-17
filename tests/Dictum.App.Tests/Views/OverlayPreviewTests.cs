using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Dictum.App.Controls;
using Dictum.App.Overlay;
using Dictum.Core.Configuration;

namespace Dictum.App.Tests.Views;

public class OverlayPreviewTests(AvaloniaFixture fx)
{
    [Fact]
    public Task ThePreviewListensToTheSimulatedVoice() => fx.Ui(() =>
    {
        var preview = new OverlayPreview { Palette = OverlayPalette.Ember, OverlayOpacity = 0.5 };
        var window = new Window { Content = preview, Width = 700, Height = 300 };
        window.Show();

        for (var i = 0; i < 20; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(OverlayPhase.Listening, preview.Canvas.Scene.Phase);
        Assert.Equal(OverlayPalette.Ember, preview.Canvas.Scene.Palette);
        Assert.InRange(preview.Canvas.LayerOpacity, 0.0, 0.5);
        window.Close();
    });

    [Theory]
    [InlineData(1.0, 150)]
    [InlineData(1.25, 174)]
    public Task ThePanelGrowsWithTheSize(double zoom, double height) => fx.Ui(() =>
    {
        var preview = new OverlayPreview { Zoom = zoom };

        preview.Measure(new Size(700, double.PositiveInfinity));

        Assert.Equal(height, preview.DesiredSize.Height);
    });
}
