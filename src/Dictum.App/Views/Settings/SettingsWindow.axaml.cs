using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Tray;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            Background = Brushes.Transparent;
        }

        // The content cross-fades 200 ms decelerate; the rail does not animate (design).
        var decelerate = new SplineEasing(0, 0, 0, 1);
        PageHost.PageTransition = new CrossFade(TimeSpan.FromMilliseconds(200)) { FadeInEasing = decelerate, FadeOutEasing = decelerate };

        // The tray's idle glyph, so the title bar and the task switcher show Dictum's mark (PR 2 deferred minor).
        Icon = new WindowIcon(TrayIconRenderer.Render(TrayGlyph.Idle, 0, 32, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true));

        Opened += (_, _) => Open.Add(this);
        Closed += (_, _) =>
        {
            Open.Remove(this);
            (DataContext as SettingsViewModel)?.Close();
        };
    }

    public static List<SettingsWindow> Open { get; } = [];
}
