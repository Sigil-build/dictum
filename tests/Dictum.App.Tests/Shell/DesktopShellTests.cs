using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Shell;
using Dictum.App.Tests.Daemon;
using Dictum.App.Tests.Settings;
using Dictum.App.Tests.Tray;
using Dictum.App.Tests.Updates;
using Dictum.App.Tray;
using Dictum.App.Updates;
using Dictum.App.ViewModels.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Setup;

namespace Dictum.App.Tests.Shell;

/// <summary>The shell with a fake daemon and a fake tray, on the headless UI thread.</summary>
public sealed class DesktopShellTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public Task TheTrayShowsTheDaemonAndQuitStopsBoth() => fx.UiAsync(async () =>
    {
        var (shell, launcher, presenter) = Create();
        var exitCode = -1;
        shell.Start(Application.Current!, code => exitCode = code);

        await WaitAsync(() => presenter.Shown.LastOrDefault()?.Tooltip == "Dictum · uk · Ctrl+Alt+Space");
        presenter.Click(presenter.Shown[^1].Menu.First(i => i.Command == TrayCommand.Quit));
        await WaitAsync(() => exitCode == 0);

        Assert.Contains(IpcCommand.Shutdown, launcher.Current!.Commands);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task WithoutATraySettingsOpensOnSystem() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create(tray: false);

        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Status.NoTray && SettingsWindow.Open.Count == 1);

        var model = (SettingsViewModel)SettingsWindow.Open[0].DataContext!;
        Assert.IsType<SystemPage>(model.Current);
        CloseSettings();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task TraySettingsHistoryAndAboutOpenOneWindowAtTheirPage() => fx.UiAsync(async () =>
    {
        var (shell, launcher, presenter) = Create();
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => presenter.Shown.Count > 0);

        presenter.Click(presenter.Shown[^1].Menu.First(i => i.Command == TrayCommand.History));
        await WaitAsync(() => SettingsWindow.Open.Count == 1);
        var model = (SettingsViewModel)SettingsWindow.Open[0].DataContext!;
        Assert.IsType<HistoryPage>(model.Current);

        presenter.Click(presenter.Shown[^1].Menu.First(i => i.Command == TrayCommand.Settings));
        await WaitAsync(() => model.Current is GeneralPage);
        Assert.Single(SettingsWindow.Open);

        presenter.Click(presenter.Shown[^1].Menu.First(i => i.Command == TrayCommand.About));
        await WaitAsync(() => model.Current is AboutPage);
        Assert.Single(SettingsWindow.Open);

        CloseSettings();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task AnOutsideEditReachesTheOpenSettings() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        shell.ShowSettings(SettingsPageKey.General);
        var model = (SettingsViewModel)SettingsWindow.Open[0].DataContext!;

        new ConfigStore(Path.Combine(_dir.Path, "config", "config.json"), NullLog.Instance)
            .Save(new DictumConfig { General = new GeneralSettings { HistorySize = 77 } });

        await WaitAsync(() => model.Session.Config.General.HistorySize == 77);
        CloseSettings();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task FramesReachTheMeter() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Supervisor.Link.State == DaemonLinkState.Connected);

        launcher.Current!.Events.Publish(new IpcEvent(IpcEventKind.Frame, Rms: 0.1f));

        await WaitAsync(() => shell.Frames.Rms(TimeSpan.FromSeconds(10)) > 0);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task AConfigWatchThatCannotStartDoesNotStopTheApp() => fx.UiAsync(async () =>
    {
        // A config path whose folder is a file: watching it fails as it does when Linux runs out of inotify instances.
        var blocker = _dir.File("not-a-folder");
        File.WriteAllText(blocker, "");
        var paths = new AppPaths(blocker, _dir.File("data"));
        var log = new DictumLog(LogLevel.Error, includeText: false, console: null, logsDirectory: null);
        var store = new ConfigStore(Path.Combine(blocker, "config.json"), log);
        await using var launcher = new FakeLauncher(_dir.File("d.sock"));
        var presenter = new TrayControllerTests.FakePresenter();
        await using var shell = new DesktopShell(paths, log, store, _dir.File("d.sock"), launcher, (_, _) => Task.FromResult<ITrayPresenter?>(presenter));

        shell.Start(Application.Current!, _ => { });

        await WaitAsync(() => presenter.Shown.LastOrDefault()?.Tooltip == "Dictum · Not set up yet");
        CloseSetup();
    });

    [Fact]
    public Task TheDaemonsEventsReachTheOverlay() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Supervisor.Link.State == DaemonLinkState.Connected);

        launcher.Current!.Events.Publish(new IpcEvent(IpcEventKind.State, State: nameof(PipelineState.Recording), Profile: "dictate"));
        await WaitAsync(() => shell.Overlay.Phase == OverlayPhase.Listening);

        launcher.Current!.Events.Publish(new IpcEvent(IpcEventKind.Stopping, Reason: StoppingReason.Shutdown));
        await WaitAsync(() => shell.Overlay.Phase == OverlayPhase.Hidden);

        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task AFirstStartWithoutAConfigOpensTheWizard() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create(config: false);

        shell.Start(Application.Current!, _ => { });

        var wizard = Assert.IsType<SetupViewModel>(Assert.Single(SetupWindow.Open).DataContext);
        Assert.Equal("Step 1 of 8 · Languages", wizard.StepLine);
        Assert.Empty(SettingsWindow.Open);
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task WithoutAConfigEverySettingsEntryOpensTheWizard() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create(config: false);
        shell.Start(Application.Current!, _ => { });
        CloseSetup();

        shell.ShowHistory();

        Assert.Single(SetupWindow.Open);
        Assert.Empty(SettingsWindow.Open);
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task ClosingAndReopeningReturnsToTheSameStep() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create(config: false);
        shell.Start(Application.Current!, _ => { });
        var wizard = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.Model.ProbeAsync();
        wizard.Model.Selected = wizard.Model.Choices[^1];
        CloseSetup();

        shell.ShowSettings();

        Assert.Same(wizard, SetupWindow.Open[0].DataContext);
        Assert.Equal((1, wizard.Model.Choices[^1].Id), (wizard.Index, wizard.Model.SpeechModel));
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task RunSetupAgainOpensTheWizardOverSettings() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        shell.ShowSettings(SettingsPageKey.General);
        var general = Assert.IsType<GeneralPage>(((SettingsViewModel)SettingsWindow.Open[0].DataContext!).Current);

        general.RunSetupCommand.Execute(null);

        Assert.Single(SetupWindow.Open);
        CloseSetup();
        CloseSettings();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // Run setup again after Settings edits shows the edits, not the walk read before them.
    [Fact]
    public Task ARerunAfterSettingsEditsShowsTheEdits() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        shell.ShowSetup();
        var first = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        CloseSetup();
        var config = DefaultConfig.Create();
        config.Profiles[0] = config.Profiles[0] with { Language = "pl" };
        new ConfigStore(new AppPaths(_dir.File("config"), _dir.File("data")).ConfigFile, NullLog.Instance).Save(config);

        shell.ShowSetup();

        var second = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        Assert.NotSame(first, second);
        Assert.Equal("pl", second.Languages.Setting);
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // The daemon's events reach the wizard, for step 8's try line.
    [Fact]
    public Task TheDaemonsEventsReachTheWizard() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Supervisor.Link.State == DaemonLinkState.Connected);
        shell.ShowSetup();
        var wizard = (SetupViewModel)SetupWindow.Open[0].DataContext!;

        launcher.Current!.Events.Publish(new IpcEvent(IpcEventKind.Finished, Outcome: DictationOutcome.Empty));

        await WaitAsync(() => wizard.Ready.TryResult == "Nothing was recognised. Speak a little closer to the microphone and try again.");
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // A GUI restarted in the middle of step 7 opens the wizard where it was, and queues nothing.
    [Fact]
    public Task AFirstStartResumesAWalkKeptOnDisk() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create(config: false);
        SavedSetupFile.Save(Paths(), KeptWalk(), NullLog.Instance);

        shell.Start(Application.Current!, _ => { });

        await WaitAsync(() => SetupWindow.Open.Count == 1);
        var wizard = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        Assert.Equal((6, "pl"), (wizard.Index, wizard.Languages.Setting));
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // Over a config the kept walk does not open on its own; Run setup again returns to it.
    [Fact]
    public Task AKeptWalkOverAConfigWaitsUntilAsked() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        SavedSetupFile.Save(Paths(), KeptWalk(), NullLog.Instance);

        shell.Start(Application.Current!, _ => { });
        Assert.Empty(SetupWindow.Open);

        shell.ShowSetup();

        Assert.Equal(6, ((SetupViewModel)SetupWindow.Open[0].DataContext!).Index);
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // Run setup again after Settings edits shows the edits, even when a walk kept at step 3 was resumed before them;
    // the stale walk is gone for good.
    [Fact]
    public Task AKeptWalkDoesNotOutliveSettingsEdits() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        var store = new ConfigStore(Paths().ConfigFile, NullLog.Instance);
        var config = DefaultConfig.Create();
        store.Save(config);
        SavedSetupFile.Save(Paths(), KeptWalk() with { KeyTyped = true, BasedOn = SavedSetupFile.Fingerprint(config) }, NullLog.Instance);
        shell.Start(Application.Current!, _ => { });
        shell.ShowSetup();
        var kept = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        Assert.Equal((2, "pl"), (kept.Index, kept.Languages.Setting));
        CloseSetup();
        config.Profiles[0] = config.Profiles[0] with { Language = "de" };
        store.Save(config);

        shell.ShowSetup();

        var fresh = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        Assert.Equal((0, "de"), (fresh.Index, fresh.Languages.Setting));
        Assert.False(File.Exists(Paths().SetupFile));
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    private AppPaths Paths() => new(_dir.File("config"), _dir.File("data"));

    private static SavedSetup KeptWalk() =>
        new("pl", "whisper-large-v3-turbo-q8", false, "http://localhost:8080/v1", "", null, false, [], false, ["whisper-large-v3-turbo-q8"]);

    // --quit reaches the running app as a command; it quits as the tray's Quit does, stopping the daemon too.
    [Fact]
    public Task AQuitCommandStopsBoth() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        var exitCode = -1;
        shell.Start(Application.Current!, code => exitCode = code);
        await WaitAsync(() => shell.Supervisor.Link.State == DaemonLinkState.Connected);

        shell.OnCommand(SingleInstance.Quit);

        await WaitAsync(() => exitCode == 0);
        Assert.Contains(IpcCommand.Shutdown, launcher.Current!.Commands);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task AQuitBeforeStartQuitsOnceStarted() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        var exitCode = -1;

        shell.OnCommand(SingleInstance.Quit);
        shell.Start(Application.Current!, code => exitCode = code);

        await WaitAsync(() => exitCode == 0);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // A tray request runs without anyone awaiting it: an answer that cannot be read must reach the log, not a faulted task.
    [Fact]
    public Task AnUnreadableAnswerToATrayRequestIsLogged() => fx.UiAsync(async () =>
    {
        var socket = _dir.File("garbled.sock");
        using var daemon = GarbledDaemon.Listen(socket);
        var output = new StringWriter();
        var log = new DictumLog(LogLevel.Warning, includeText: false, console: TextWriter.Synchronized(output), logsDirectory: null);
        var paths = new AppPaths(_dir.File("config"), _dir.File("data"));
        await using var launcher = new FakeLauncher(socket);
        await using var shell = new DesktopShell(paths, log, new ConfigStore(paths.ConfigFile, log), socket, launcher,
            (_, _) => Task.FromResult<ITrayPresenter?>(null));

        shell.UseProfile("uk");

        await WaitAsync(() => output.ToString().Contains($"'{IpcCommand.UseProfile}'", StringComparison.Ordinal));
    });

    /// <summary>Answers every request with a line that is not JSON.</summary>
    private sealed class GarbledDaemon : IDisposable
    {
        private readonly Socket _listener;

        private GarbledDaemon(Socket listener) => _listener = listener;

        public static GarbledDaemon Listen(string path)
        {
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen();
            var daemon = new GarbledDaemon(listener);
            _ = Task.Run(daemon.ServeAsync);
            return daemon;
        }

        public void Dispose() => _listener.Dispose();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptAsync();
                    await using var stream = new NetworkStream(client);
                    using var reader = new StreamReader(stream);
                    await reader.ReadLineAsync();
                    await stream.WriteAsync("not json\n"u8.ToArray());
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // The test is over and closed the listener.
            }
        }
    }

    // The check runs on the shell's timers: found, the version is in the tray menu and in one notification.
    [Fact]
    public Task AFoundUpdateIsInTheTrayAndNotifiesOnce() => fx.UiAsync(async () =>
    {
        var (updates, timers) = Updates("v0.5.0");
        var popup = new FakePopup();
        var (shell, launcher, presenter) = Create(updates: updates, popup: popup);
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => presenter.Shown.Count > 0);
        Assert.True(updates.Enabled);

        timers.Fire();
        timers.Fire();

        await WaitAsync(() => presenter.Shown[^1].Menu.Any(i => i.Label == "Update available: 0.5.0"));
        var shown = Assert.Single(popup.Shown);
        Assert.Equal(("Dictum 0.5.0 is available", "Click to open the release page and download it."), (shown.Title, shown.Text));
        Assert.Same(presenter, shown.Tray);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task TurningTheCheckOffInTheConfigStopsItsTimers() => fx.UiAsync(async () =>
    {
        var (updates, timers) = Updates("v0.5.0");
        var (shell, launcher, _) = Create(updates: updates, popup: new FakePopup());
        shell.Start(Application.Current!, _ => { });
        Assert.True(updates.Enabled);

        new ConfigStore(Paths().ConfigFile, NullLog.Instance).Save(new DictumConfig { General = new GeneralSettings { CheckForUpdates = false } });

        await WaitAsync(() => !updates.Enabled);
        Assert.Equal(0, timers.Pending);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task AConfigWithTheCheckOffNeverStartsIt() => fx.UiAsync(async () =>
    {
        var (updates, timers) = Updates("v0.5.0");
        var (shell, launcher, _) = Create(config: false, updates: updates, popup: new FakePopup());
        new ConfigStore(Paths().ConfigFile, NullLog.Instance).Save(new DictumConfig { General = new GeneralSettings { CheckForUpdates = false } });

        shell.Start(Application.Current!, _ => { });

        Assert.Equal((false, 0), (updates.Enabled, timers.Pending));
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // A version found in an earlier run is in the menu from the start, before any check of this run.
    [Fact]
    public Task AnUpdateFoundBeforeARestartIsInTheMenuAtOnce() => fx.UiAsync(async () =>
    {
        new UpdateCheckFile(Paths().UpdateCheckFile, NullLog.Instance).Save(new UpdateCheckState
        {
            LastChecked = DateTimeOffset.UtcNow,
            Latest = "0.5.0",
            Page = "https://github.com/Sigil-build/dictum/releases/tag/v0.5.0",
            Notified = "0.5.0",
        });
        var (updates, _) = Updates("v0.5.0");
        var (shell, launcher, presenter) = Create(updates: updates, popup: new FakePopup());

        shell.Start(Application.Current!, _ => { });

        await WaitAsync(() => presenter.Shown.Count > 0);
        Assert.Contains(presenter.Shown[^1].Menu, i => i.Command == TrayCommand.OpenUpdate);
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    private (UpdateChecker Checker, ManualTimers Timers) Updates(string tag)
    {
        var timers = new ManualTimers();
        var checker = new UpdateChecker(
            new FakeReleases { Next = FakeReleases.Found(tag) }, new UpdateCheckFile(Paths().UpdateCheckFile, NullLog.Instance), timers,
            TimeProvider.System, "0.4.0", NullLog.Instance);
        return (checker, timers);
    }

    private sealed class FakePopup : IUpdatePopup
    {
        public List<(ITrayPresenter? Tray, string Title, string Text, Action Clicked)> Shown { get; } = [];

        public void Show(ITrayPresenter? tray, string title, string text, Action clicked) => Shown.Add((tray, title, text, clicked));
    }

    private (DesktopShell Shell, FakeLauncher Launcher, TrayControllerTests.FakePresenter Presenter) Create(
        bool tray = true, bool config = true, UpdateChecker? updates = null, IUpdatePopup? popup = null)
    {
        var paths = new AppPaths(_dir.File("config"), _dir.File("data"));
        var log = new DictumLog(LogLevel.Error, includeText: false, console: null, logsDirectory: null);
        var store = new ConfigStore(paths.ConfigFile, log);
        if (config)
        {
            store.Save(new DictumConfig());
        }

        var socket = _dir.File("d.sock");
        var launcher = new FakeLauncher(socket);
        var presenter = new TrayControllerTests.FakePresenter();
        var shell = new DesktopShell(paths, log, store, socket, launcher, (_, _) => Task.FromResult<ITrayPresenter?>(tray ? presenter : null),
            probeHardware: () => new HardwareInfo("Test GPU", 0, 4L * 1024 * 1024 * 1024), updates: updates, updatePopup: popup);
        return (shell, launcher, presenter);
    }

    private static void CloseSetup()
    {
        foreach (var window in SetupWindow.Open.ToList())
        {
            window.Close();
        }
    }

    private static void CloseSettings()
    {
        foreach (var window in SettingsWindow.Open.ToList())
        {
            window.Close();
        }
    }

    // Awaiting on the headless UI thread lets posted work run between checks.
    private static Task WaitAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string what = "") =>
        Eventually.WaitForAsync(condition, what);

    [Fact]
    public Task TheRailListsThePagesInTheDesignsOrder() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });

        shell.ShowSettings(SettingsPageKey.Profiles);
        var model = (SettingsViewModel)SettingsWindow.Open[0].DataContext!;

        Assert.Equal(["General", "Profiles", "Speech providers", "Processors", "Models", "System", "History", "About Dictum"], model.Pages.Select(p => p.Title));
        Assert.IsType<ProfilesPage>(model.Current);
        CloseSettings();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    [Fact]
    public Task TheModelsPageGetsTheShellsProbe() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });

        shell.ShowSettings(SettingsPageKey.Models);
        var model = (SettingsViewModel)SettingsWindow.Open[0].DataContext!;
        var page = Assert.IsType<ModelsPage>(model.Current);
        await page.ProbeAsync();

        Assert.StartsWith("GPU found: Test GPU · 4.0 GB VRAM · ", page.HardwareText);
        Assert.Equal(3, page.Cards.Count);
        CloseSettings();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });
}
