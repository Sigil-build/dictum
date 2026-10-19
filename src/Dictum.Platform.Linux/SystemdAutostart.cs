namespace Dictum.Platform.Linux;

/// <summary>
/// The daemon alone at login: a systemd user unit bound to the graphical session. Plasma 6 and GNOME export
/// WAYLAND_DISPLAY and DISPLAY to systemd, so the unit sees the session. <paramref name="systemctl"/> runs
/// <c>systemctl --user</c> with the given arguments.
/// </summary>
public sealed class SystemdAutostart(string configHome, Func<IReadOnlyList<string>, CancellationToken, Task> systemctl)
{
    public const string UnitName = "dictum.service";

    private const string DaemonReload = "daemon-reload";
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(10);

    public SystemdAutostart()
        : this(DefaultConfigHome, Systemctl)
    {
    }

    public static string DefaultConfigHome =>
        Environment.GetEnvironmentVariable(LinuxEnvironment.ConfigHome) is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), LinuxEnvironment.DefaultConfigFolder);

    public string UnitPath => Path.Combine(configHome, "systemd", "user", UnitName);

    public bool IsInstalled => File.Exists(UnitPath);

    public async Task InstallAsync(LoginItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        await File.WriteAllTextAsync(UnitPath, UnitFile(item), ct).ConfigureAwait(false);
        await systemctl([DaemonReload], ct).ConfigureAwait(false);
        await systemctl(["enable", UnitName], ct).ConfigureAwait(false);
    }

    public async Task UninstallAsync(CancellationToken ct)
    {
        if (File.Exists(UnitPath))
        {
            await systemctl(["disable", UnitName], ct).ConfigureAwait(false);
            File.Delete(UnitPath);
            await systemctl([DaemonReload], ct).ConfigureAwait(false);
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
        var result = await Tools.RunAsync("systemctl", ["--user", .. args], null, ct, SystemctlTimeout).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemctl --user {string.Join(' ', args)} failed: {result.Error.Trim()}");
        }
    }
}
