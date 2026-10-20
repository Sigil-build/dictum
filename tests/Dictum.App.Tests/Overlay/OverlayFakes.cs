using Avalonia;
using Avalonia.Controls;
using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

/// <summary>Records what the controller asks of the window; its screens are two monitors measured on KDE.</summary>
internal sealed class FakeSurface : IOverlaySurface
{
    public List<PixelRect> Presented { get; } = [];

    public int Dismissed { get; private set; }

    public Func<OverlayScene>? Next { get; private set; }

    public double Zoom { get; private set; }

    public double Opacity { get; private set; }

    public IReadOnlyList<ScreenArea> ScreenAreas { get; } =
    [
        new("DP-6", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1394), 1, true),
        new("DP-2", new PixelRect(2560, 0, 2560, 1440), new PixelRect(2560, 0, 2560, 1394), 1.25, false),
    ];

    public event Action? ScreensChanged;

    public void Present(PixelRect bounds, double zoom, double opacity, Func<OverlayScene> nextScene)
    {
        Presented.Add(bounds);
        Zoom = zoom;
        Opacity = opacity;
        Next = nextScene;
    }

    public void Dismiss()
    {
        Dismissed++;
        Next = null;
    }

    public void ChangeScreens() => ScreensChanged?.Invoke();
}

/// <summary>Answers the probe at once, or not at all when <see cref="Slow"/>.</summary>
internal sealed class FakeDesktop(Func<OverlayProbe> probe) : IOverlayDesktop
{
    public bool Slow { get; set; }

    public void Prepare(Window window)
    {
    }

    public Task<OverlayProbe> ProbeAsync(CancellationToken ct) =>
        Slow ? Task.FromCanceled<OverlayProbe>(new CancellationToken(canceled: true)) : Task.FromResult(probe());
}
