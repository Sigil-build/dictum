using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    private static readonly TimeSpan PageFade = TimeSpan.FromMilliseconds(200);

    public SettingsWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this);

        // The content cross-fades, decelerating; the rail does not animate.
        var decelerate = new SplineEasing(0, 0, 0, 1);
        PageHost.PageTransition = new CrossFade(PageFade) { FadeInEasing = decelerate, FadeOutEasing = decelerate };

        Opened += (_, _) => Open.Add(this);
        Closed += (_, _) =>
        {
            Open.Remove(this);
            (DataContext as SettingsViewModel)?.Close();
        };
    }

    public static List<SettingsWindow> Open { get; } = [];
}
