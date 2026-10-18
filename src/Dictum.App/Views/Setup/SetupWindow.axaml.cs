using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Dictum.App.Tray;
using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Views.Setup;

/// <summary>The setup wizard's 640 × 600 dialog (spec 8). Esc closes and keeps what was entered; Enter is Next when it is on.</summary>
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            Background = Brushes.Transparent;
        }

        Icon = new WindowIcon(TrayIconRenderer.Render(TrayGlyph.Idle, 0, 32, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true));
        KeyDown += OnKeyDown;
        Opened += (_, _) => Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
    }

    public static List<SetupWindow> Open { get; } = [];

    /// <summary>Keys the focused control did not take: Esc closes; Enter is Next, except in a box that takes new lines (SPEC:147).</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key == Key.Enter
            && DataContext is SetupViewModel wizard
            && FocusManager?.GetFocusedElement() is not TextBox { AcceptsReturn: true }
            && wizard.NextCommand.CanExecute(null))
        {
            e.Handled = true;
            _ = wizard.NextCommand.ExecuteAsync(null);
        }
    }
}
