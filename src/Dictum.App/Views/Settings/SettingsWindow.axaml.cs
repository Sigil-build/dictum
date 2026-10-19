using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Tray;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    /// <summary>Mica needs Windows 11, whose builds start here.</summary>
    private const int Windows11Build = 22000;

    private const int IconSize = 32;

    private static readonly TimeSpan PageFade = TimeSpan.FromMilliseconds(200);

    public SettingsWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build))
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            Background = Brushes.Transparent;
        }

        // The content cross-fades, decelerating; the rail does not animate.
        var decelerate = new SplineEasing(0, 0, 0, 1);
        PageHost.PageTransition = new CrossFade(PageFade) { FadeInEasing = decelerate, FadeOutEasing = decelerate };

        // The tray's idle glyph, so the title bar and the task switcher show Dictum's mark.
        Icon = new WindowIcon(TrayIconRenderer.Render(TrayGlyph.Idle, frame: 0, IconSize, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true));

        Opened += (_, _) => Open.Add(this);
        Closed += (_, _) =>
        {
            Open.Remove(this);
            (DataContext as SettingsViewModel)?.Close();
        };
    }

    public static List<SettingsWindow> Open { get; } = [];
}
