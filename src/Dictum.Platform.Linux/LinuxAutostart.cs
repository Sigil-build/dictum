namespace Dictum.Platform.Linux;

/// <summary>
/// Login start on Linux. The desktop app gets an XDG autostart entry, because it needs the graphical session;
/// the daemon alone gets a systemd user unit (<see cref="SystemdAutostart"/>). Installing one removes the other.
/// </summary>
public sealed class LinuxAutostart(SystemdAutostart daemon, string configHome) : IAutostart
{
    public LinuxAutostart()
        : this(new SystemdAutostart(), SystemdAutostart.DefaultConfigHome)
    {
    }

    public string AutostartFile => Path.Combine(configHome, "autostart", "dictum.desktop");

    public LoginApp? Installed =>
        File.Exists(AutostartFile) ? LoginApp.DesktopApp : daemon.IsInstalled ? LoginApp.Daemon : null;

    public string Location(LoginApp app) => app == LoginApp.DesktopApp ? AutostartFile : daemon.UnitPath;

    public static string AutostartEntry(string executable) => $"""
        [Desktop Entry]
        Type=Application
        Name=Dictum
        Comment=Voice to text
        Exec="{executable}"
        Icon=audio-input-microphone
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    public async Task InstallAsync(LoginItem item, CancellationToken ct)
    {
        if (item.App == LoginApp.DesktopApp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            await File.WriteAllTextAsync(AutostartFile, AutostartEntry(item.Executable), ct).ConfigureAwait(false);
            await daemon.UninstallAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await daemon.InstallAsync(item.Executable, ct).ConfigureAwait(false);
            RemoveEntry();
        }
    }

    public async Task UninstallAsync(CancellationToken ct)
    {
        RemoveEntry();
        await daemon.UninstallAsync(ct).ConfigureAwait(false);
    }

    // File.Delete throws on Windows when the folder is missing; the tests run there too.
    private void RemoveEntry()
    {
        if (File.Exists(AutostartFile))
        {
            File.Delete(AutostartFile);
        }
    }
}
