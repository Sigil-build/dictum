using System.Globalization;

namespace Dictum.Platform.Linux.X11;

/// <summary>
/// KDE tells X11 apps that "apply scaling themselves" their scale through Xft.dpi (96 × scale). Avalonia's X11 backend
/// reports scale 1 there, so this passes the scale on as <c>AVALONIA_GLOBAL_SCALE_FACTOR</c> before Avalonia starts.
/// </summary>
public static class DisplayScale
{
    public const string Variable = "AVALONIA_GLOBAL_SCALE_FACTOR";

    /// <summary>The factor to set; null leaves Avalonia alone (no X display, a scale already in the environment, or 96 dpi).</summary>
    public static string? FactorFor(Func<string, string?> environment, double? xftDpi)
    {
        if (environment(Variable) is { Length: > 0 }
            || environment("AVALONIA_SCREEN_SCALE_FACTORS") is { Length: > 0 }
            || environment("DISPLAY") is not { Length: > 0 }
            || xftDpi is not { } dpi
            || Math.Abs(dpi - 96) < 0.5)
        {
            return null;
        }

        return Math.Round(dpi / 96, 3).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Sets the variable for this process. Call it before Avalonia starts.</summary>
    public static void Apply()
    {
        try
        {
            if (FactorFor(Environment.GetEnvironmentVariable, XResources.Dpi(X11Overlay.ResourceDatabase())) is { } factor)
            {
                Environment.SetEnvironmentVariable(Variable, factor);
            }
        }
        catch (DllNotFoundException)
        {
            // No libX11: Avalonia cannot use X11 either.
        }
    }
}
