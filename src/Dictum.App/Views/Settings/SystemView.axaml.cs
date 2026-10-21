using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Dictum.App.Views.Settings;

public sealed partial class SystemView : UserControl
{
    /// <summary>A row's name and status take the detail's column too when the row has no detail, so a path has room.</summary>
    public static readonly IValueConverter StatusSpan = new FuncValueConverter<string?, int>(detail => detail is null ? 2 : 1);

    /// <summary>A status or a detail as shown: a path in it wraps after a folder, never inside a name.</summary>
    public static readonly IValueConverter Breakable = new FuncValueConverter<string?, string?>(text => text is null ? null : UiText.BreakablePaths(text));

    public SystemView() => InitializeComponent();
}
