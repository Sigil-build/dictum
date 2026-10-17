using Avalonia;
using Avalonia.Threading;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Shell;
using Dictum.App.Tests.Daemon;
using Dictum.App.Tray;
using Dictum.App.Views;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.Tests.Shell;

/// <summary>The shell with a fake daemon and a fake tray, on the headless UI thread.</summary>
public sealed class DesktopShellTests(AvaloniaFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-shell", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public Task TheTrayShowsTheDaemonAndQuitStopsBoth() => fx.UiAsync(async () =>
    {
        var (shell, launcher, presenter) = Create();
        var exitCode = -1;
        shell.Start(Application.Current!, code => exitCode = code);

        await WaitAsync(() => presenter.Shown.LastOrDefault()?.Tooltip == "Dictum · uk · Ctrl+Alt+Space");
        presenter.Click(presenter.Shown[^1].Menu.First(i => i.Command == TrayCommand.Quit));
        await WaitAsync(() => exitCode == 0);

        Assert.Contains("shutdown", launcher.Current!.Commands);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task WithoutATrayTheStatusWindowOpens() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create(tray: false);

        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Status.NoTray);

        Assert.Contains(StatusWindow.Open, w => w.IsVisible);
        foreach (var window in StatusWindow.Open.ToList())
        {
            window.Close();
        }

        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task AConfigWatchThatCannotStartDoesNotStopTheApp() => fx.UiAsync(async () =>
    {
        // A config path whose folder is a file: watching it fails as it does when Linux runs out of inotify instances.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "");
        var paths = new AppPaths(blocker, Path.Combine(_dir, "data"));
        var log = new DictumLog(LogLevel.Error, includeText: false, console: null, logsDirectory: null);
        var store = new ConfigStore(Path.Combine(blocker, "config.json"), log);
        await using var launcher = new FakeLauncher(Path.Combine(_dir, "d.sock"));
        var presenter = new TrayControllerTests.FakePresenter();
        await using var shell = new DesktopShell(paths, log, store, Path.Combine(_dir, "d.sock"), launcher, (_, _) => Task.FromResult<ITrayPresenter?>(presenter));

        shell.Start(Application.Current!, _ => { });

        await WaitAsync(() => presenter.Shown.LastOrDefault()?.Tooltip == "Dictum · Not set up yet");
    });

    [Fact]
    public Task TheDaemonsEventsReachTheOverlay() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Supervisor.Link.State == DaemonLinkState.Connected);

        launcher.Current!.Events.Publish(new IpcEvent("state", State: "Recording", Profile: "dictate"));
        await WaitAsync(() => shell.Overlay.Phase == OverlayPhase.Listening);

        launcher.Current!.Events.Publish(new IpcEvent("stopping", Reason: "shutdown"));
        await WaitAsync(() => shell.Overlay.Phase == OverlayPhase.Hidden);

        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    private (DesktopShell Shell, FakeLauncher Launcher, TrayControllerTests.FakePresenter Presenter) Create(bool tray = true)
    {
        var paths = new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));
        var log = new DictumLog(LogLevel.Error, includeText: false, console: null, logsDirectory: null);
        var store = new ConfigStore(paths.ConfigFile, log);
        store.Save(new DictumConfig());
        var socket = Path.Combine(_dir, "d.sock");
        var launcher = new FakeLauncher(socket);
        var presenter = new TrayControllerTests.FakePresenter();
        var shell = new DesktopShell(paths, log, store, socket, launcher, (_, _) => Task.FromResult<ITrayPresenter?>(tray ? presenter : null));
        return (shell, launcher, presenter);
    }

    // Awaiting on the headless UI thread lets posted work run between checks.
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }
}
