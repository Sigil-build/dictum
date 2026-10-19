namespace Dictum.Platform.Linux;

/// <summary>
/// The daemon alone at login: a systemd user unit bound to the graphical session. Plasma 6 and GNOME export
/// WAYLAND_DISPLAY and DISPLAY to systemd, so the unit sees the session. <paramref name="systemctl"/> runs
/// <c>systemctl --user</c> with the given arguments.
/// </summary>
public sealed class SystemdAutostart(string configHome, Func<IReadOnlyList<string>, CancellationToken, Task> systemctl)
{
    public const string UnitName = "dictum.service";

    public SystemdAutostart()
        : this(DefaultConfigHome, Systemctl)
    {
    }

    public static string DefaultConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    public string UnitPath => Path.Combine(configHome, "systemd", "user", UnitName);

    public bool IsInstalled => File.Exists(UnitPath);

    public async Task InstallAsync(LoginItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        await File.WriteAllTextAsync(UnitPath, UnitFile(item), ct).ConfigureAwait(false);
        await systemctl(["daemon-reload"], ct).ConfigureAwait(false);
        await systemctl(["enable", UnitName], ct).ConfigureAwait(false);
    }

    public async Task UninstallAsync(CancellationToken ct)
    {
        if (File.Exists(UnitPath))
        {
            await systemctl(["disable", UnitName], ct).ConfigureAwait(false);
            File.Delete(UnitPath);
            await systemctl(["daemon-reload"], ct).ConfigureAwait(false);
        }
    }

    public static string UnitFile(LoginItem item) => $"""
        [Unit]
        Description=Dictum voice-to-text daemon
        PartOf=graphical-session.target
        After=graphical-session.target

        [Service]
        ExecStart={item.CommandLine}
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

/// <summary>
/// ~/.local/share/applications/dev.dictum.Dictum.desktop, hidden from menus. The portal looks Dictum's app id up
/// there, so the daemon writes it before it registers.
/// </summary>
public static class DesktopEntry
{
    public static string EntryPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                ? data
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications",
            $"{DBus.Portal.AppId}.desktop");

    public static string Content(LoginItem daemon) => $"""
        [Desktop Entry]
        Type=Application
        Name=Dictum
        Comment=Voice to text
        Exec={daemon.CommandLine}
        Icon=audio-input-microphone
        Terminal=false
        NoDisplay=true
        Categories=Utility;Accessibility;

        """;

    /// <summary>Writes the entry if it is missing or points at another executable.</summary>
    public static async Task WriteAsync(LoginItem daemon, CancellationToken ct)
    {
        var content = Content(daemon);
        if (File.Exists(EntryPath) && await File.ReadAllTextAsync(EntryPath, ct).ConfigureAwait(false) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        await File.WriteAllTextAsync(EntryPath, content, ct).ConfigureAwait(false);
    }
}
