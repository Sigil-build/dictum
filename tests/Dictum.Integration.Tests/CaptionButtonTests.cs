using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Dictum.App.Resources;
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

    // Narrator reads each caption button by its name in the UI's language, Restore once the window is maximised, and Tab
    // passes them by, as it does Windows' own; a click on one leaves the focus where it was.
    [Theory]
    [InlineData(WindowState.Normal, "en")]
    [InlineData(WindowState.Maximized, "en")]
    [InlineData(WindowState.Normal, "uk")]
    [InlineData(WindowState.Maximized, "uk")]
    public async Task TheCaptionButtonsHaveNamesAndTakeNoFocus(WindowState state, string language)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: elsewhere the window manager draws the title bar");
            return;
        }

        var culture = CultureInfo.GetCultureInfo(language);
        var (buttons, tabbedTo) = await RealAvalonia.RunAsync(async () =>
        {
            var before = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = culture;
            try
            {
                var window = new SettingsWindow { ShowActivated = false };
                window.Show();
                window.WindowState = state;
                await RealAvalonia.SettleAsync();
                var shown = CaptionButtons(window).ToList();
                var described = string.Join(", ", shown.Select(b =>
                    $"{ControlAutomationPeer.CreatePeerForElement(b).GetName()} {(b.Focusable ? "focusable" : "not focusable")}"));
                var tabbedTo = TabStops(window).Count(shown.Contains);
                window.Close();
                return (described, tabbedTo);
            }
            finally
            {
                CultureInfo.CurrentUICulture = before;
            }
        });

        string Text(string key) => Strings.ResourceManager.GetString(key, culture)!;
        var middle = Text(state == WindowState.Maximized ? nameof(Strings.CaptionRestore) : nameof(Strings.CaptionMaximize));
        Assert.Equal(
            $"{Text(nameof(Strings.CaptionMinimize))} not focusable, {middle} not focusable, {Text(nameof(Strings.CaptionClose))} not focusable",
            buttons);
        Assert.Equal(0, tabbedTo);
    }

    /// <summary>The caption buttons Avalonia shows, left to right, with their edges in the window's coordinates.</summary>
    private static string Buttons(Window window)
    {
        var shown = CaptionButtons(window)
            .Select(b => (Name: b.Name!["PART_".Length..^"Button".Length], TopLeft: b.TranslatePoint(default, window)!.Value, b.Bounds.Size));
        return string.Join(", ", shown.Select(b =>
            $"{b.Name} {b.TopLeft.X}-{b.TopLeft.X + b.Size.Width} {b.TopLeft.Y}-{b.TopLeft.Y + b.Size.Height}"));
    }

    /// <summary>The caption buttons Avalonia shows, left to right.</summary>
    private static IEnumerable<Button> CaptionButtons(Window window) =>
        window.GetVisualParent()!.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton"
                or "PART_FullScreenButton")
            .OrderBy(b => b.TranslatePoint(default, window)!.Value.X);

    /// <summary>Where Tab goes from the window's first stop, once round.</summary>
    private static List<IInputElement> TabStops(Window window)
    {
        var focus = window.FocusManager!;
        var stops = new List<IInputElement>();
        var next = focus.FindFirstFocusableElement();
        while (next is not null && !stops.Contains(next) && stops.Count < 500)
        {
            stops.Add(next);
            next = focus.FindNextElement(NavigationDirection.Next, new FindNextElementOptions { FocusedElement = next });
        }

        return stops;
    }
}
