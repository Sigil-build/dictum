using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    // A headless window, like one rendered in software, answers a Mica request with None.
    [Theory]
    [MemberData(nameof(Windows))]
    public Task AWindowWithoutMicaHasItsOwnColour(string name) => fx.Ui(() =>
    {
        var window = Create(name);
        window.Show();

        Assert.NotEqual(WindowTransparencyLevel.Mica, window.ActualTransparencyLevel);
        Assert.Same(Base(), window.Background);
        window.Close();
    });
}
