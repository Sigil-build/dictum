using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;

namespace Dictum.Integration.Tests;

/// <summary>
/// The caption buttons Avalonia draws for Dictum's windows on Windows, whose client area extends into the title bar. Windows
/// 11 draws its own 46 px wide and touching, with Close in the window's top-right corner. Avalonia's headless platform
/// draws no window decorations, so these are real windows, shown without activation.
/// </summary>
public class CaptionButtonTests
{
    private const double TitleBarHeight = 40;

    [Theory]
    [InlineData("settings", WindowState.Normal)]
    [InlineData("settings", WindowState.Maximized)]
    [InlineData("setup", WindowState.Normal)]
    public async Task TheCaptionButtonsSitInTheTopRightCornerAsWindowsDrawsThem(string name, WindowState state)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: elsewhere the window manager draws the title bar");
            return;
        }

        var (width, buttons) = await RealAvalonia.RunAsync(async () =>
        {
            Window window = name == "settings" ? new SettingsWindow() : new SetupWindow();
            window.ShowActivated = false;
            window.Show();
            window.WindowState = state;
            await RealAvalonia.SettleAsync();
            var measured = (window.ClientSize.Width, Buttons(window));
            window.Close();
            return measured;
        });

        Assert.Equal(
            $"Minimize {width - 138}-{width - 92} 0-{TitleBarHeight}, Maximize {width - 92}-{width - 46} 0-{TitleBarHeight}, "
            + $"Close {width - 46}-{width} 0-{TitleBarHeight}",
            buttons);
    }

    /// <summary>The caption buttons Avalonia shows, left to right, with their edges in the window's coordinates.</summary>
    private static string Buttons(Window window)
    {
        var host = window.GetVisualParent()!;
        var shown = host.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton"
                or "PART_FullScreenButton")
            .Select(b => (Name: b.Name!["PART_".Length..^"Button".Length], TopLeft: b.TranslatePoint(default, window)!.Value, b.Bounds.Size))
            .OrderBy(b => b.TopLeft.X);
        return string.Join(", ", shown.Select(b =>
            $"{b.Name} {b.TopLeft.X}-{b.TopLeft.X + b.Size.Width} {b.TopLeft.Y}-{b.TopLeft.Y + b.Size.Height}"));
    }
}
