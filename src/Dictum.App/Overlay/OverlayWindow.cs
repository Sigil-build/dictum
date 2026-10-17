using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Dictum.App.Overlay;

/// <summary>What the controller needs from the overlay window; a fake in tests.</summary>
public interface IOverlaySurface
{
    IReadOnlyList<ScreenArea> ScreenAreas { get; }

    /// <summary>Shows or moves the window, and draws the scene <paramref name="next"/> returns on every frame until it is Hidden.</summary>
    void Present(PixelRect bounds, double zoom, double opacity, Func<OverlayScene> next);

    /// <summary>Hides the window now.</summary>
    void Dismiss();

    /// <summary>Screens were added, removed or rescaled (UI thread).</summary>
    event Action? ScreensChanged;
}

/// <summary>
/// The transparent, click-through, never-activated topmost window that holds the <see cref="OverlayCanvas"/>
/// (spec 5.4). The OS part (extended styles on Windows, override-redirect on X11) arrives through <c>prepare</c>
/// before the window is first shown. While shown, it draws on display frames, about 60 times a second at most.
/// </summary>
public sealed class OverlayWindow : Window, IOverlaySurface
{
    private Func<OverlayScene>? _next;
    private TimeSpan? _lastFrame;
    private bool _looping;
    private double _opacity = 1;

    public OverlayWindow(Action<IPlatformHandle> prepare)
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
        Title = "Dictum";
        Content = Canvas;

        // The native window exists from construction on; X11 needs override-redirect before the first map.
        if (TryGetPlatformHandle() is { } handle)
        {
            prepare(handle);
        }

        Screens.Changed += (_, _) => ScreensChanged?.Invoke();
    }

    public event Action? ScreensChanged;

    public OverlayCanvas Canvas { get; } = new();

    /// <summary>Display frames closer together than this are skipped (ruling: about 60 fps).</summary>
    public TimeSpan MinFrameInterval { get; set; } = TimeSpan.FromMilliseconds(15);

    public IReadOnlyList<ScreenArea> ScreenAreas =>
        [.. Screens.All.Select(s => new ScreenArea(s.DisplayName, s.Bounds, s.WorkingArea, s.Scaling, s.IsPrimary))];

    public void Present(PixelRect bounds, double zoom, double opacity, Func<OverlayScene> next)
    {
        _next = next;
        _opacity = opacity;
        Canvas.Zoom = zoom;
        Position = bounds.Position;
        if (!IsVisible)
        {
            Canvas.Scene = next();
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

    private void OnFrame(TimeSpan time)
    {
        if (_next is not { } next)
        {
            _looping = false;
            return;
        }

        if (_lastFrame is { } last && time >= last && time - last < MinFrameInterval)
        {
            RequestAnimationFrame(OnFrame);
            return;
        }

        _lastFrame = time;
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
