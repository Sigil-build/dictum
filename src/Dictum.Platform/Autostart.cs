namespace Dictum.Platform;

/// <summary>What starts at login.</summary>
public enum LoginApp
{
    /// <summary><c>dictum daemon</c> alone, for machines without the desktop app.</summary>
    Daemon,

    /// <summary><c>dictum-gui</c>, which starts the daemon itself.</summary>
    DesktopApp,
}

/// <param name="Executable">Full path of <c>dictum</c> for <see cref="LoginApp.Daemon"/>, of <c>dictum-gui</c> otherwise;
/// inside an AppImage, the AppImage file.</param>
/// <param name="AppImage">The item runs through an AppImage, whose AppRun starts the CLI when the first argument is
/// <see cref="InstallLocation.AppImageCli"/>.</param>
public sealed record LoginItem(LoginApp App, string Executable, bool AppImage = false)
{
    public IReadOnlyList<string> Arguments => App == LoginApp.Daemon
        ? AppImage ? [InstallLocation.AppImageCli, DictumExecutables.DaemonVerb] : [DictumExecutables.DaemonVerb]
        : [];

    /// <summary>The quoted executable, then the arguments: the Run key value, the unit's ExecStart, an entry's Exec.</summary>
    public string CommandLine => string.Join(' ', [$"\"{Executable}\"", .. Arguments]);

    /// <summary>
    /// The desktop app when it sits next to <paramref name="dictum"/>, the daemon alone otherwise. Inside an AppImage
    /// (<paramref name="appImage"/>) the desktop app is always bundled, and the AppImage file is what lasts.
    /// </summary>
    public static LoginItem NextTo(string dictum, string? appImage = null)
    {
        if (appImage is not null)
        {
            return InstallLocation.DesktopApp(dictum, appImage);
        }

        var app = Path.Combine(Path.GetDirectoryName(dictum) ?? ".", DictumExecutables.FileName(DictumExecutables.DesktopApp));
        return File.Exists(app) ? new LoginItem(LoginApp.DesktopApp, app) : new LoginItem(LoginApp.Daemon, dictum);
    }
}

/// <summary>Starts Dictum when the user logs in. Only one kind of entry exists at a time.</summary>
public interface IAutostart
{
    /// <summary>Where the entry for <paramref name="app"/> lives, e.g. the unit file or the registry value.</summary>
    string Location(LoginApp app);

    /// <summary>What starts at login now, or null.</summary>
    LoginApp? Installed { get; }

    /// <summary>Writes the entry for <paramref name="item"/> and removes an entry of the other kind.</summary>
    Task InstallAsync(LoginItem item, CancellationToken ct);

    /// <summary>Removes every Dictum login entry.</summary>
    Task UninstallAsync(CancellationToken ct);
}
