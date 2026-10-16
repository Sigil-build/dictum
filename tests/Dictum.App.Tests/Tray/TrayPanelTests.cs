using Avalonia.Platform;
using Dictum.App.Tray;

namespace Dictum.App.Tests.Tray;

public class TrayPanelTests
{
    [Theory]
    [InlineData(false, PlatformThemeVariant.Dark, true, true)]
    [InlineData(false, PlatformThemeVariant.Light, false, false)]
    [InlineData(true, PlatformThemeVariant.Dark, true, false)]
    [InlineData(true, PlatformThemeVariant.Light, false, true)]
    public void TheIconColourFollowsWhatPaintsThePanel(bool highContrast, PlatformThemeVariant contrastTheme, bool taskbarLight, bool light)
    {
        // Rows 3 and 4: Night sky on a light Windows mode paints a dark taskbar; Desert on a dark mode paints a light one.
        var colours = new PlatformColorValues
        {
            ContrastPreference = highContrast ? ColorContrastPreference.High : ColorContrastPreference.NoPreference,
            ThemeVariant = contrastTheme,
        };

        Assert.Equal(light, TrayPanel.IsLight(colours, () => taskbarLight));
    }

    [Fact]
    public void WithoutPlatformColoursTheTaskbarModeDecides() => Assert.True(TrayPanel.IsLight(null, () => true));
}
