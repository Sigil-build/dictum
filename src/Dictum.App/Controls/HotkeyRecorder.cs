using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Dictum.App.Resources;
using Dictum.App.Settings;

namespace Dictum.App.Controls;

/// <summary>
/// The hotkey recorder. It shows the hotkey as keycaps; a click starts listening.
/// <list type="bullet">
/// <item>The next chord replaces the hotkey. Modifiers alone keep listening.</item>
/// <item>Esc cancels; Backspace or Delete alone removes the hotkey.</item>
/// <item>Losing focus or leaving the window cancels.</item>
/// </list>
/// <see cref="IsRecording"/> is two-way, so the page can pause the daemon's hotkeys while it listens.
/// </summary>
public sealed class HotkeyRecorder : Button
{
    public static readonly StyledProperty<string?> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyRecorder, string?>(nameof(Hotkey), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsRecordingProperty =
        AvaloniaProperty.Register<HotkeyRecorder, bool>(nameof(IsRecording), defaultBindingMode: BindingMode.TwoWay);

    private const double PartSpacing = 6;
    private const double RecordingDotSize = 8;

    private PhysicalKey? _ending;

    public HotkeyRecorder()
    {
        Classes.Add("hotkey");
        Click += (_, _) => IsRecording = true;
        Show();
    }

    public string? Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool IsRecording
    {
        get => GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HotkeyProperty || change.Property == IsRecordingProperty)
        {
            PseudoClasses.Set(":recording", IsRecording);
            Show();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.PhysicalKey == PhysicalKey.Tab)
        {
            // Tab stops listening and moves focus on, as it does everywhere else.
            IsRecording = false;
            return;
        }

        e.Handled = true;
        if (e.KeyModifiers == KeyModifiers.None && e.PhysicalKey == PhysicalKey.Escape)
        {
            End(e.PhysicalKey);
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.PhysicalKey is PhysicalKey.Backspace or PhysicalKey.Delete)
        {
            Hotkey = null;
            End(e.PhysicalKey);
        }
        else if (HotkeyKeys.Chord(e.PhysicalKey, e.KeyModifiers) is { } chord)
        {
            Hotkey = chord.ToString();
            End(e.PhysicalKey);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // A Space or Enter release must not click the button again while it listens.
        if (IsRecording)
        {
            e.Handled = true;
            return;
        }

        // The release of the key that ended listening must not click the button again, as a Space release would.
        if (_ending == e.PhysicalKey)
        {
            _ending = null;
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        IsRecording = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        IsRecording = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void End(PhysicalKey key)
    {
        _ending = key;
        IsRecording = false;
    }

    private void Show()
    {
        if (IsRecording)
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = PartSpacing,
                Children =
                {
                    new Ellipse { Width = RecordingDotSize, Height = RecordingDotSize, Classes = { "recordingDot" }, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Strings.SettingsHotkeyListening, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            AutomationProperties.SetName(this, Strings.SettingsHotkeyListening);
            return;
        }

        if (Hotkey is null)
        {
            Content = new TextBlock { Text = Strings.SettingsHotkeyNone, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(this, Strings.SettingsHotkeyAccessibleNone);
            return;
        }

        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = PartSpacing };
        var parts = Hotkey.Split(Dictum.Core.Hotkeys.Hotkey.Separator);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                keys.Children.Add(new TextBlock { Text = "+", Classes = { "keycapPlus" }, VerticalAlignment = VerticalAlignment.Center });
            }

            keys.Children.Add(new Border { Classes = { "keycap" }, Child = new TextBlock { Text = parts[i] } });
        }

        Content = keys;
        AutomationProperties.SetName(this, UiText.Format(Strings.SettingsHotkeyAccessible, HotkeyKeys.Spoken(Hotkey)));
    }
}
