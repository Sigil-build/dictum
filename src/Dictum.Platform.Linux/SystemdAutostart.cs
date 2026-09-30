namespace Dictum.Platform.Linux;

/// <summary>
/// A systemd user unit bound to the graphical session, plus the .desktop file portals use to identify
/// the app. Plasma 6 and GNOME export WAYLAND_DISPLAY/DISPLAY to systemd, so the unit sees the session.
/// </summary>
public sealed class SystemdAutostart : IAutostart
{
    public const string UnitName = "dictum.service";

    private static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    public static string UnitPath => Path.Combine(ConfigHome, "systemd", "user", UnitName);

    public string Location => UnitPath;

    public bool IsInstalled => File.Exists(UnitPath);

    public async Task InstallAsync(string executable, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        await File.WriteAllTextAsync(UnitPath, UnitFile(executable), ct).ConfigureAwait(false);
        await DesktopEntry.WriteAsync(executable, ct).ConfigureAwait(false);
        await Systemctl(["daemon-reload"], ct).ConfigureAwait(false);
        await Systemctl(["enable", UnitName], ct).ConfigureAwait(false);
    }

    public async Task UninstallAsync(CancellationToken ct)
    {
        if (File.Exists(UnitPath))
        {
            await Systemctl(["disable", UnitName], ct).ConfigureAwait(false);
            File.Delete(UnitPath);
            await Systemctl(["daemon-reload"], ct).ConfigureAwait(false);
        }
    }

    public static string UnitFile(string executable) => $"""
        [Unit]
        Description=Dictum voice-to-text daemon
        PartOf=graphical-session.target
        After=graphical-session.target

        [Service]
        ExecStart="{executable}" daemon
        Restart=on-failure
        RestartSec=3

        [Install]
        WantedBy=graphical-session.target

        """;

    private static async Task Systemctl(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await Tools.RunAsync("systemctl", ["--user", .. args], null, ct, 10_000).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemctl --user {string.Join(' ', args)} failed: {result.Error.Trim()}");
        }
    }
}

/// <summary>~/.local/share/applications/dev.dictum.Dictum.desktop, hidden from menus.</summary>
public static class DesktopEntry
{
    public static string EntryPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                ? data
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications",
            $"{DBus.Portal.AppId}.desktop");

    public static string Content(string executable) => $"""
        [Desktop Entry]
        Type=Application
        Name=Dictum
        Comment=Voice to text
        Exec="{executable}" daemon
        Icon=audio-input-microphone
        Terminal=false
        NoDisplay=true
        Categories=Utility;Accessibility;

        """;

    /// <summary>Writes the entry if it is missing or points at another executable.</summary>
    public static async Task WriteAsync(string executable, CancellationToken ct)
    {
        var content = Content(executable);
        if (File.Exists(EntryPath) && await File.ReadAllTextAsync(EntryPath, ct).ConfigureAwait(false) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        await File.WriteAllTextAsync(EntryPath, content, ct).ConfigureAwait(false);
    }
}
