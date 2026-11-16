using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Tray;

namespace Dictum.App.Views;

/// <summary>
/// What Dictum's windows share: their own colour, Mica behind the content where Windows 11 has it, and Dictum's mark as
/// the window icon. The colour comes from the <c>chrome</c> style in Resources/Controls.axaml.
/// </summary>
internal static class WindowChrome
{
    /// <summary>Gives the window its own colour; Resources/Controls.axaml styles it.</summary>
    public const string ChromeClass = "chrome";

    /// <summary>Set while Avalonia draws Mica behind the window, which then lets it through.</summary>
    public const string MicaClass = "mica";

    /// <summary>Mica needs Windows 11, whose builds start here.</summary>
    private const int Windows11Build = 22000;

    private const int IconSize = 32;

    public static void Apply(Window window)
    {
        window.Classes.Add(ChromeClass);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build))
        {
            // Mica is only a request: Avalonia draws it when it renders on the GPU, and answers None when it renders in
            // software (a VM without 3D, Remote Desktop, a blocked driver) or loses the GPU while the window is open. The
            // background turns transparent only while Mica is really there, or the window would show black.
            window.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == TopLevel.ActualTransparencyLevelProperty)
                {
                    ShowMica(window);
                }
            };
            ShowMica(window);
        }

        // The tray's idle glyph, so the title bar and the task switcher show Dictum's mark.
        window.Icon = new WindowIcon(TrayIconRenderer.Render(TrayGlyph.Idle, frame: 0, IconSize, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true));
    }

    private static void ShowMica(Window window) =>
        window.Classes.Set(MicaClass, window.ActualTransparencyLevel == WindowTransparencyLevel.Mica);
}
