using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dictum.App.Controls;

/// <summary>
/// A settings card: an icon, a label and a description on the left, the row's control on the right (its content), an
/// inline error under the description, and an optional second line (<see cref="Footer"/>, the microphone's meter).
/// </summary>
public sealed class SettingsRow : ContentControl
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<SettingsRow, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<SettingsRow, string?>(nameof(Label));

    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingsRow, string?>(nameof(Description));

    public static readonly StyledProperty<string?> ErrorProperty = AvaloniaProperty.Register<SettingsRow, string?>(nameof(Error));

    public static readonly StyledProperty<object?> FooterProperty = AvaloniaProperty.Register<SettingsRow, object?>(nameof(Footer));

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string? Error
    {
        get => GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
