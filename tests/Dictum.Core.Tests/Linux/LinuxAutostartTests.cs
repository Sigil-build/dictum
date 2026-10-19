using Dictum.Platform;
using Dictum.Platform.Linux;
using Dictum.Testing;

namespace Dictum.Core.Tests.Linux;

/// <summary>File work only, with systemctl recorded: runs on every OS.</summary>
public sealed class LinuxAutostartTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly TempDirectory _home = new();
    private readonly List<string> _systemctl = [];

    public void Dispose() => _home.Dispose();

    [Fact]
    public async Task TheDesktopAppGetsAnXdgEntryAndReplacesTheDaemonUnit()
    {
        var autostart = Autostart();
        await autostart.InstallAsync(new LoginItem(LoginApp.Daemon, "/opt/dictum/dictum"), _ct);

        await autostart.InstallAsync(new LoginItem(LoginApp.DesktopApp, "/opt/dictum/dictum-gui"), _ct);

        Assert.Equal(LoginApp.DesktopApp, autostart.Installed);
        Assert.False(File.Exists(Path.Combine(_home.Path, "systemd", "user", "dictum.service")));
        Assert.Contains("Exec=\"/opt/dictum/dictum-gui\"", File.ReadAllText(Path.Combine(_home.Path, "autostart", "dictum.desktop")));
        Assert.Contains("disable dictum.service", _systemctl);
    }

    [Fact]
    public async Task TheDaemonUnitReplacesTheDesktopAppEntry()
    {
        var autostart = Autostart();
        await autostart.InstallAsync(new LoginItem(LoginApp.DesktopApp, "/opt/dictum/dictum-gui"), _ct);

        await autostart.InstallAsync(new LoginItem(LoginApp.Daemon, "/opt/dictum/dictum"), _ct);

        Assert.Equal(LoginApp.Daemon, autostart.Installed);
        Assert.False(File.Exists(Path.Combine(_home.Path, "autostart", "dictum.desktop")));
        Assert.Equal(["daemon-reload", "enable dictum.service"], _systemctl);
    }

    [Fact]
    public async Task UninstallRemovesBoth()
    {
        var autostart = Autostart();
        await autostart.InstallAsync(new LoginItem(LoginApp.Daemon, "/opt/dictum/dictum"), _ct);
        Directory.CreateDirectory(Path.Combine(_home.Path, "autostart"));
        File.WriteAllText(autostart.AutostartFile, LinuxAutostart.AutostartEntry(new LoginItem(LoginApp.DesktopApp, "/opt/dictum/dictum-gui")));

        await autostart.UninstallAsync(_ct);

        Assert.Null(autostart.Installed);
        Assert.Equal(autostart.AutostartFile, autostart.Location(LoginApp.DesktopApp));
    }

    [Fact]
    public void TheAutostartEntryStartsTheDesktopApp()
    {
        var entry = LinuxAutostart.AutostartEntry(new LoginItem(LoginApp.DesktopApp, "/home/me/Dictum/dictum-gui"));

        Assert.Contains("Exec=\"/home/me/Dictum/dictum-gui\"", entry);
        Assert.Contains("Type=Application", entry);
        Assert.DoesNotContain(" daemon", entry);
    }

    [Fact]
    public async Task TheDaemonUnitRunsThroughTheAppImage()
    {
        var autostart = Autostart();

        await autostart.InstallAsync(InstallLocation.Daemon("/tmp/.mount_x/usr/bin/dictum", "/home/me/Apps/dictum.AppImage"), _ct);

        Assert.Contains("ExecStart=\"/home/me/Apps/dictum.AppImage\" dictum daemon", File.ReadAllText(Path.Combine(_home.Path, "systemd", "user", "dictum.service")));
    }

    private LinuxAutostart Autostart()
    {
        var daemon = new SystemdAutostart(_home.Path, (args, _) =>
        {
            _systemctl.Add(string.Join(' ', args));
            return Task.CompletedTask;
        });
        return new LinuxAutostart(daemon, _home.Path);
    }
}
