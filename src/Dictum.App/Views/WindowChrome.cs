using Avalonia.Controls;
using Avalonia.Media;
using Dictum.App.Tray;

namespace Dictum.App.Views;

/// <summary>
/// What Dictum's windows share: their own colour, Mica behind the content where Windows 11 has it, and Dictum's mark as
/// the window icon. The colour comes from the <c>chrome</c> style in Resources/Controls.axaml.
/// </summary>
public static class WindowChrome
{
    /// <summary>Gives the window its own colour; Resources/Controls.axaml styles it.</summary>
    public const string ChromeClass = "chrome";

    /// <summary>Set while Avalonia draws Mica behind the window, which then lets it through.</summary>
    public const string MicaClass = "mica";

    /// <summary>Mica needs Windows 11, whose builds start here.</summary>
    private const int Windows11Build = 22000;

    private const int IconSize = 32;

    public static void Apply(Window window) => Apply(window, offerMica: OperatingSystem.IsWindowsVersionAtLeast(10, 0, Windows11Build));

    /// <param name="offerMica">Whether to ask for Mica: on Windows 11 only.</param>
    public static void Apply(Window window, bool offerMica)
    {
        window.Classes.Add(ChromeClass);
        if (offerMica)
        {
            // Mica is only a request: Avalonia draws it when it renders on the GPU, and answers None when it renders in
            // software (a VM without 3D, Remote Desktop, a blocked driver) or loses the GPU while the window is open. The
            // background turns transparent only while Mica is really there, or the window would show black.
            window.TransparencyLevelHint = [WindowTransparencyLevel.Mica];
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == TopLevel.ActualTransparencyLevelProperty)
                {
                    ShowMica(window, window.ActualTransparencyLevel);
                }
            };
            ShowMica(window, window.ActualTransparencyLevel);
        }

        // The tray's idle glyph, so the title bar and the task switcher show Dictum's mark.
        window.Icon = new WindowIcon(TrayIconRenderer.Render(TrayGlyph.Idle, frame: 0, IconSize, Color.Parse(TrayGeometry.MonoOnDark), colourLayer: true));
    }

    /// <summary>Lets Mica through while <paramref name="level"/> says Avalonia draws it.</summary>
    public static void ShowMica(Window window, WindowTransparencyLevel level) =>
        window.Classes.Set(MicaClass, level == WindowTransparencyLevel.Mica);
}
