using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dictum.App.Controls;

/// <summary>A token brush for a control that draws itself, attached or not (tests and the self-test render detached controls).</summary>
internal static class ThemeBrush
{
    public static IBrush Find(Control control, string key)
    {
        if (control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out value) && value is IBrush appBrush
            ? appBrush
            : Brushes.Gray;
    }
}
