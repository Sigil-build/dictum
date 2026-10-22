using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
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

    // Headless windows draw no decorations of their own, so the theme's template is built the way a window builds it.
    private static (WindowDrawnDecorations Decorations, Dictionary<string, CaptionButton> Buttons) Realise()
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
        return (decorations, buttons);
    }

    private static Color GlyphColor(CaptionButton button) =>
        ((ISolidColorBrush)button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Fill!).Color;

    [Fact]
    public Task TheCloseGlyphTurnsWhiteUnderThePointer() => fx.Ui(() =>
    {
        var (_, buttons) = Realise();
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
        var (_, buttons) = Realise();
        var maximize = buttons["PART_MaximizeButton"];
        Assert.Equal(1, maximize.Opacity);

        maximize.IsEnabled = false;

        Assert.Equal(0.2, maximize.Opacity);
    });

    [Fact]
    public Task AButtonAPlatformDoesNotAllowIsHidden() => fx.Ui(() =>
    {
        var (decorations, buttons) = Realise();
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
}
