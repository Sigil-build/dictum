using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.LogicalTree;

namespace Dictum.App.Controls;

/// <summary>
/// A caption button of Dictum's window decorations (Resources/WindowDecorations.axaml), which a screen reader names in
/// the UI's language, as Windows names its own: Avalonia names the buttons it finds in the decorations in English, and
/// Maximize stays Maximize when the window is maximised. It is <c>:inactive</c> while its window is, so the theme can
/// dim its glyph as Windows 11 does.
/// </summary>
[PseudoClasses(Inactive)]
public sealed class CaptionButton : Button
{
    public const string Inactive = ":inactive";

    public static readonly StyledProperty<string?> CaptionProperty = AvaloniaProperty.Register<CaptionButton, string?>(nameof(Caption));

    public static readonly StyledProperty<string?> MaximizedCaptionProperty =
        AvaloniaProperty.Register<CaptionButton, string?>(nameof(MaximizedCaption));

    public static readonly StyledProperty<bool> IsWindowMaximizedProperty =
        AvaloniaProperty.Register<CaptionButton, bool>(nameof(IsWindowMaximized));

    /// <summary>
    /// The window it belongs to. Avalonia's decorations say nothing of whether it is active (no pseudo-class), and draw
    /// their buttons above the window in its host's visual tree, so the window is found as a logical ancestor.
    /// </summary>
    private WindowBase? _window;

    /// <summary>Its name: Minimize, Maximize or Close.</summary>
    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Its name while the window is maximised, Restore for the maximize button; null keeps <see cref="Caption"/>.</summary>
    public string? MaximizedCaption
    {
        get => GetValue(MaximizedCaptionProperty);
        set => SetValue(MaximizedCaptionProperty, value);
    }

    /// <summary>Set by the decorations' <c>:maximized</c> style.</summary>
    public bool IsWindowMaximized
    {
        get => GetValue(IsWindowMaximizedProperty);
        set => SetValue(IsWindowMaximizedProperty, value);
    }

    /// <summary>What a screen reader says for the button.</summary>
    public string? SpokenName => Spoken(IsWindowMaximized);

    protected override AutomationPeer OnCreateAutomationPeer() => new CaptionButtonAutomationPeer(this);

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        _window = this.FindLogicalAncestorOfType<WindowBase>();
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
        }

        ShowActivation();
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        base.OnDetachedFromLogicalTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Maximize becomes Restore under the pointer: a screen reader on it hears the new name.
        if (change.Property == IsWindowMaximizedProperty && MaximizedCaption is not null && ControlAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, Spoken(!IsWindowMaximized), SpokenName);
        }
    }

    private string? Spoken(bool maximized) => maximized && MaximizedCaption is { } restore ? restore : Caption;

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.IsActiveProperty)
        {
            ShowActivation();
        }
    }

    private void ShowActivation() => PseudoClasses.Set(Inactive, _window is { IsActive: false });

    private sealed class CaptionButtonAutomationPeer(CaptionButton owner) : ButtonAutomationPeer(owner)
    {
        protected override string? GetNameCore() => owner.SpokenName ?? base.GetNameCore();
    }
}
