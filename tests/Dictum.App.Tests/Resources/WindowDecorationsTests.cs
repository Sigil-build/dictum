using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;
using Dictum.App.Controls;

namespace Dictum.App.Tests.Resources;

/// <summary>
/// The decorations theme styles the caption buttons through their template. They are CaptionButtons, and an Avalonia type
/// selector matches a control's style key exactly, so a selector that says Button reaches none of them.
/// </summary>
public sealed class WindowDecorationsTests(AvaloniaFixture fx)
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Attach")]
    private static extern void Attach(WindowDrawnDecorations decorations, Window window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ApplyTemplate")]
    private static extern void ApplyTemplate(WindowDrawnDecorations decorations);

    // What the platform calls when another window takes the focus, and when the window gets it back; a headless window
    // keeps it.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HandleDeactivated")]
    private static extern void Deactivate(WindowBase window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HandleActivated")]
    private static extern void Activate(WindowBase window);

    // Headless windows draw no decorations of their own, so the theme's template is built the way a window builds it.
    private static (Window Window, WindowDrawnDecorations Decorations, Dictionary<string, CaptionButton> Buttons) Realise()
    {
        var theme = (ControlTheme)Application.Current!.FindResource(typeof(WindowDrawnDecorations))!;
        var decorations = new WindowDrawnDecorations { Theme = theme };
        var window = new Window { Width = 400, Height = 200 };
        window.Show();
        ((ISetLogicalParent)decorations).SetParent(window);
        Attach(decorations, window);
        ApplyTemplate(decorations);
        Assert.NotNull(decorations.Template);
        Assert.NotNull(decorations.Content);
        window.Content = decorations.Content!.Overlay;
        // The title bar part is a window's to enable; the buttons are measured only when their row is visible.
        window.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "PART_OverlayPanel").IsVisible = true;
        window.Show();
        window.UpdateLayout();
        var buttons = window.GetVisualDescendants().OfType<CaptionButton>().ToDictionary(b => b.Name!);
        return (window, decorations, buttons);
    }

    private static Color GlyphColor(CaptionButton button) =>
        ((ISolidColorBrush)button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Fill!).Color;

    /// <summary>A colour token in the window's theme.</summary>
    private static Color Token(Window window, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(window.ActualThemeVariant, key)).Color;

    [Fact]
    public Task TheCloseGlyphTurnsWhiteUnderThePointer() => fx.Ui(() =>
    {
        var (_, _, buttons) = Realise();
        var close = buttons["PART_CloseButton"];
        var minimize = buttons["PART_MinimizeButton"];
        Assert.NotEqual(Colors.White, GlyphColor(close));

        ((IPseudoClasses)close.Classes).Set(":pointerover", true);
        ((IPseudoClasses)minimize.Classes).Set(":pointerover", true);

        Assert.Equal(Colors.White, GlyphColor(close));
        Assert.NotEqual(Colors.White, GlyphColor(minimize));
    });

    [Fact]
    public Task ADisabledCaptionButtonIsDimmed() => fx.Ui(() =>
    {
        var (_, _, buttons) = Realise();
        var maximize = buttons["PART_MaximizeButton"];
        Assert.Equal(1, maximize.Opacity);

        maximize.IsEnabled = false;

        Assert.Equal(0.2, maximize.Opacity);
    });

    [Fact]
    public Task AButtonAPlatformDoesNotAllowIsHidden() => fx.Ui(() =>
    {
        var (_, decorations, buttons) = Realise();
        var pseudo = (IPseudoClasses)decorations.Classes;
        pseudo.Set(":has-minimize", true);
        pseudo.Set(":has-maximize", true);
        Assert.True(buttons["PART_MinimizeButton"].IsVisible);
        Assert.True(buttons["PART_MaximizeButton"].IsVisible);

        pseudo.Set(":has-minimize", false);
        pseudo.Set(":has-maximize", false);

        Assert.False(buttons["PART_MinimizeButton"].IsVisible);
        Assert.False(buttons["PART_MaximizeButton"].IsVisible);
    });

    // As Windows 11's own: while another window is active, the glyphs dim, but not the one under the pointer, and Close's
    // turns white on its red as ever; they come back when the window is active again.
    [Fact]
    public Task TheGlyphsDimWhileTheWindowIsInactive() => fx.Ui(() =>
    {
        var (window, _, buttons) = Realise();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsActive);
        var active = Token(window, "color.text.primary");
        var inactive = Token(window, "color.caption.inactive");
        Assert.NotEqual(active, inactive);
        Assert.All(buttons.Values, b => Assert.Equal(active, GlyphColor(b)));

        Deactivate(window);

        Assert.False(window.IsActive);
        Assert.All(buttons.Values, b => Assert.Equal(inactive, GlyphColor(b)));
        ((IPseudoClasses)buttons["PART_MinimizeButton"].Classes).Set(":pointerover", true);
        ((IPseudoClasses)buttons["PART_CloseButton"].Classes).Set(":pointerover", true);
        Assert.Equal(active, GlyphColor(buttons["PART_MinimizeButton"]));
        Assert.Equal(Colors.White, GlyphColor(buttons["PART_CloseButton"]));
        Assert.Equal(inactive, GlyphColor(buttons["PART_MaximizeButton"]));

        ((IPseudoClasses)buttons["PART_MinimizeButton"].Classes).Set(":pointerover", false);
        ((IPseudoClasses)buttons["PART_CloseButton"].Classes).Set(":pointerover", false);
        Activate(window);

        Assert.All(buttons.Values, b => Assert.Equal(active, GlyphColor(b)));
    });
}
