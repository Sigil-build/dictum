using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

public class OverlayWindowTests(AvaloniaFixture fx)
{
    private static readonly OverlayScene Listening = OverlayScene.Hidden with { Phase = OverlayPhase.Listening, Appear = 1 };

    [Fact]
    public Task TheWindowIsTransparentTopmostAndNeverActivated() => fx.Ui(() =>
    {
        var window = new OverlayWindow(_ => { });

        Assert.Equal(WindowDecorations.None, window.WindowDecorations);
        Assert.False(window.ShowActivated);
        Assert.False(window.ShowInTaskbar);
        Assert.True(window.Topmost);
        Assert.Contains(WindowTransparencyLevel.Transparent, window.TransparencyLevelHint);
        Assert.Same(window.Canvas, window.Content);
    });

    [Fact]
    public Task PresentShowsTheWindowAndDrawsUntilTheSceneIsHidden() => fx.Ui(() =>
    {
        var window = new OverlayWindow(_ => { }) { MinFrameInterval = TimeSpan.Zero };
        var scenes = new Queue<OverlayScene>([Listening, Listening, Listening]);

        window.Present(new PixelRect(100, 200, 480, 96), 1, 0.8, () => scenes.Count > 0 ? scenes.Dequeue() : OverlayScene.Hidden);

        Assert.True(window.IsVisible);
        Assert.Equal(new PixelPoint(100, 200), window.Position);
        Assert.Equal(0.8, window.Canvas.LayerOpacity, 6);
        for (var i = 0; i < 20 && window.IsVisible; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(window.IsVisible);
        Assert.Equal(OverlayPhase.Hidden, window.Canvas.Scene.Phase);
    });

    [Fact]
    public Task DismissHidesAtOnce() => fx.Ui(() =>
    {
        var window = new OverlayWindow(_ => { });
        window.Present(new PixelRect(0, 0, 480, 96), 1, 1, () => Listening);

        window.Dismiss();

        Assert.False(window.IsVisible);
    });

    [Fact]
    public Task TheBlindDesktopKnowsNothing() => fx.UiAsync(async () =>
        Assert.Equal(OverlayProbe.Unknown, await OverlayDesktop.None.ProbeAsync(CancellationToken.None)));

    [Fact]
    public Task ThisDesktopAnswersWithoutThrowing() => fx.UiAsync(async () =>
    {
        using var desktop = OverlayDesktop.ForThisDesktop(new ListLog());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var probe = await desktop.ProbeAsync(timeout.Token);

        Assert.NotNull(probe);
    });
}
