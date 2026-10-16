using Avalonia.Controls;
using Avalonia.Media;

namespace Dictum.App.Views;

public partial class StatusWindow : Window
{
    public StatusWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // Mica shows only through a transparent background; Fluent paints its fallback if the OS turns Mica off (research §3).
            TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            Background = Brushes.Transparent;
        }

        Opened += (_, _) => Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
    }

    /// <summary>The status windows on screen; the shell keeps at most one.</summary>
    public static List<StatusWindow> Open { get; } = [];
}
