using Avalonia.Controls;
using Avalonia.Input;
using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Views.Setup;

/// <summary>The setup wizard's dialog. Esc closes and keeps what was entered; Enter is Next when it is on.</summary>
public sealed partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        KeyDown += OnKeyDown;
        Opened += (_, _) => Open.Add(this);
        Closed += (_, _) => Open.Remove(this);
    }

    public static List<SetupWindow> Open { get; } = [];

    /// <summary>Keys the focused control did not take: Esc closes; Enter is Next, except in a box that takes new lines.</summary>
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
