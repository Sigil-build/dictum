using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Views;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;

namespace Dictum.App.Tests.Views;

/// <summary>
/// Settings and the wizard show their own colour unless Windows 11's Mica is really drawn behind them. Avalonia draws
/// Mica only when it renders on the GPU; on software rendering (a VM without 3D, Remote Desktop, a blocked driver) it
/// answers the request with no transparency, and a transparent background there shows black.
/// </summary>
public sealed class WindowChromeTests(AvaloniaFixture fx)
{
    public static TheoryData<string> Windows => ["settings", "setup"];

    private static Window Create(string name) => name == "settings" ? new SettingsWindow() : new SetupWindow();

    private static IBrush Base() =>
        Application.Current!.TryGetResource("color.bg.base", Application.Current.ActualThemeVariant, out var brush) && brush is IBrush solid
            ? solid
            : throw new InvalidOperationException("color.bg.base is missing");

    private static Color Colour(Window window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    [Theory]
    [MemberData(nameof(Windows))]
    public Task SettingsAndTheWizardHaveTheirOwnColour(string name) => fx.Ui(() =>
    {
        var window = Create(name);
        window.Show();

        Assert.NotEqual(WindowTransparencyLevel.Mica, window.ActualTransparencyLevel);
        Assert.Same(Base(), window.Background);
        window.Close();
    });

    // On any OS, as on Windows 11: a headless window, like one rendered in software, answers a Mica request with None.
    [Fact]
    public Task AWindowThatAsksForMicaAndGetsNoneKeepsItsColour() => fx.Ui(() =>
    {
        var window = new Window();
        WindowChrome.Apply(window, offerMica: true);
        window.Show();

        Assert.Equal([WindowTransparencyLevel.Mica], window.TransparencyLevelHint);
        Assert.NotEqual(WindowTransparencyLevel.Mica, window.ActualTransparencyLevel);
        Assert.Same(Base(), window.Background);
        window.Close();
    });

    // Mica can come after the window opens, and go while it is open, when Avalonia loses the GPU and falls back.
    [Fact]
    public Task TheColourGivesWayToMicaOnlyWhileMicaIsDrawn() => fx.Ui(() =>
    {
        var window = new Window();
        WindowChrome.Apply(window, offerMica: true);
        window.Show();

        WindowChrome.ShowMica(window, WindowTransparencyLevel.Mica);
        Assert.Equal(Colors.Transparent, Colour(window));

        WindowChrome.ShowMica(window, WindowTransparencyLevel.None);
        Assert.Same(Base(), window.Background);
        window.Close();
    });

    [Fact]
    public Task AWindowNotOfferedMicaAsksForNothing() => fx.Ui(() =>
    {
        var window = new Window();
        WindowChrome.Apply(window, offerMica: false);
        window.Show();

        Assert.Empty(window.TransparencyLevelHint);
        Assert.Same(Base(), window.Background);
        Assert.NotNull(window.Icon);
        window.Close();
    });
}
