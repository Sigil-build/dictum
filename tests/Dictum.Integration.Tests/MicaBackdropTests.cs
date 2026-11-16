using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;

namespace Dictum.Integration.Tests;

/// <summary>
/// Where Avalonia draws Windows 11's Mica, Settings and the wizard let it through; Dictum.App.Tests checks that a window
/// without Mica keeps its own colour. Real windows, shown without activation, so enabled with DICTUM_INTEGRATION=1.
/// </summary>
public class MicaBackdropTests
{
    [Theory]
    [InlineData("settings")]
    [InlineData("setup")]
    public async Task WhereMicaIsDrawnTheWindowLetsItThrough(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000), "Windows 11 only: Mica needs it");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1", "Set DICTUM_INTEGRATION=1 to run: it shows real windows");

        var (level, background) = await RealAvalonia.RunAsync(async () =>
        {
            Window window = name == "settings" ? new SettingsWindow() : new SetupWindow();
            window.ShowActivated = false;
            window.Show();
            await RealAvalonia.SettleAsync();
            var seen = (window.ActualTransparencyLevel, window.Background);
            window.Close();
            return seen;
        });

        // Software rendering, as in a VM without 3D or over Remote Desktop, draws no Mica; App.Tests covers that case.
        Assert.SkipUnless(level == WindowTransparencyLevel.Mica, $"Avalonia draws no Mica here ({level})");
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(background).Color);
    }
}
