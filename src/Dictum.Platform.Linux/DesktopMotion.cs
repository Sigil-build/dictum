using System.Globalization;

namespace Dictum.Platform.Linux;

/// <summary>The desktop's reduced-motion setting: KDE's animation speed at "instant", GNOME's animations off.</summary>
public static class DesktopMotion
{
    private const string AnimationFactorKey = "AnimationDurationFactor=";
    private static readonly TimeSpan GsettingsTimeout = TimeSpan.FromSeconds(1);

    /// <summary>KDE: <c>AnimationDurationFactor=0</c> in the <c>[KDE]</c> group of <c>kdeglobals</c>.</summary>
    public static bool KdeReduced(string? kdeglobals)
    {
        var group = "";
        foreach (var raw in (kdeglobals ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                group = line;
            }
            else if (group == "[KDE]" && line.StartsWith(AnimationFactorKey, StringComparison.Ordinal))
            {
                return double.TryParse(line[AnimationFactorKey.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)
                    && factor <= 0;
            }
        }

        return false;
    }

    /// <summary>GNOME: <c>gsettings get org.gnome.desktop.interface enable-animations</c> prints <c>false</c>.</summary>
    public static bool GnomeReduced(string? gsettingsOutput) => gsettingsOutput?.Trim() == "false";

    /// <summary>Reads the setting for this session; false when it cannot be read.</summary>
    public static async Task<bool> ReducedAsync(SessionInfo session, Func<string, string?> environment, CancellationToken ct)
    {
        try
        {
            switch (session.Desktop)
            {
                case Desktop.Kde:
                {
                    var file = Path.Combine(LinuxEnvironment.ConfigDirectory(environment), "kdeglobals");
                    return File.Exists(file) && KdeReduced(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                }

                case Desktop.Gnome:
                {
                    var result = await Tools.RunAsync("gsettings", ["get", "org.gnome.desktop.interface", "enable-animations"], null, ct, GsettingsTimeout)
                        .ConfigureAwait(false);
                    return result.ExitCode == 0 && GnomeReduced(result.Output);
                }

                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException
            or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
