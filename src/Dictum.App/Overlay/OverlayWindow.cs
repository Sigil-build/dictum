using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dictum.App.Overlay;

/// <summary>What the controller needs from the overlay window; a fake in tests.</summary>
public interface IOverlaySurface
{
    IReadOnlyList<ScreenArea> ScreenAreas { get; }

    /// <summary>Shows or moves the window, and draws the scene <paramref name="nextScene"/> returns on every frame until it is Hidden.</summary>
    void Present(PixelRect bounds, double zoom, double opacity, Func<OverlayScene> nextScene);

    /// <summary>Hides the window now.</summary>
    void Dismiss();

    /// <summary>Screens were added, removed or rescaled (UI thread).</summary>
    event Action? ScreensChanged;
}

/// <summary>
/// The transparent, click-through, never-activated topmost window that holds the <see cref="OverlayCanvas"/>. The OS
/// part (extended styles on Windows, override-redirect on X11) arrives through <c>prepare</c> before the window is
/// first shown. While shown, it draws on every display frame.
/// </summary>
public sealed class OverlayWindow : Window, IOverlaySurface
{
    private Func<OverlayScene>? _next;
    private bool _looping;
    private double _opacity = 1;

    public OverlayWindow(Action<Window> prepare)
    {
        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = AppNames.Product;
        Content = Canvas;

        // Before the first Show: X11 needs override-redirect before the first map, Win32 its style callback.
        prepare(this);

        Screens.Changed += (_, _) => ScreensChanged?.Invoke();
    }

    public event Action? ScreensChanged;

    public OverlayCanvas Canvas { get; } = new();

    public IReadOnlyList<ScreenArea> ScreenAreas =>
        [.. Screens.All.Select(s => new ScreenArea(s.DisplayName, s.Bounds, s.WorkingArea, s.Scaling, s.IsPrimary))];

    public void Present(PixelRect bounds, double zoom, double opacity, Func<OverlayScene> nextScene)
    {
        _next = nextScene;
        _opacity = opacity;
        Canvas.Zoom = zoom;
        Position = bounds.Position;
        if (!IsVisible)
        {
            Canvas.Scene = nextScene();
            Canvas.LayerOpacity = _opacity * Canvas.Scene.Appear;
            Show();
        }

        if (!_looping)
        {
            _looping = true;
            RequestAnimationFrame(OnFrame);
        }
    }

    public void Dismiss()
    {
        _next = null;
        Canvas.Scene = OverlayScene.Hidden;
        Hide();
    }

    /// <summary>
    /// Every display frame: a skipped callback invalidates nothing, and Avalonia then waits for its
    /// own ~16 ms timer (about 31 ms on Win32) instead of the next vsync.
    /// </summary>
    private void OnFrame(TimeSpan time)
    {
        if (_next is not { } next)
        {
            _looping = false;
            return;
        }

        var scene = next();
        if (scene.Phase is OverlayPhase.Hidden)
        {
            _looping = false;
            Dismiss();
            return;
        }

        Canvas.LayerOpacity = _opacity * scene.Appear;
        Canvas.Scene = scene;
        RequestAnimationFrame(OnFrame);
    }
}
