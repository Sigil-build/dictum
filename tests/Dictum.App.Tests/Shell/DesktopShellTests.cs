using Avalonia;
using Avalonia.Threading;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.App.Tests.Daemon;
using Dictum.App.Tray;
using Dictum.App.ViewModels.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Setup;

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
    public Task TraySettingsAndHistoryOpenOneWindowAtTheirPage() => fx.UiAsync(async () =>
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

        new ConfigStore(Path.Combine(_dir, "config", "config.json"), NullLog.Instance)
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

        launcher.Current!.Events.Publish(new IpcEvent("frame", Rms: 0.1f));

        await WaitAsync(() => shell.Frames.Rms(TimeSpan.FromSeconds(10)) > 0);
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
        CloseSetup();
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

    // Spec 3.2.
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

    // Review focus 4.
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

    // Spec 7.
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

    // Final review I1: Run setup again after Settings edits shows the edits, not the walk read before them.
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
        new ConfigStore(new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data")).ConfigFile, NullLog.Instance).Save(config);

        shell.ShowSetup();

        var second = (SetupViewModel)SetupWindow.Open[0].DataContext!;
        Assert.NotSame(first, second);
        Assert.Equal("pl", second.Languages.Setting);
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // PR 5b: the daemon's events reach the wizard, for step 8's try line.
    [Fact]
    public Task TheDaemonsEventsReachTheWizard() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });
        await WaitAsync(() => shell.Supervisor.Link.State == DaemonLinkState.Connected);
        shell.ShowSetup();
        var wizard = (SetupViewModel)SetupWindow.Open[0].DataContext!;

        launcher.Current!.Events.Publish(new IpcEvent("finished", Outcome: DictationOutcome.Empty));

        await WaitAsync(() => wizard.Ready.TryResult == "Nothing was recognised. Speak a little closer to the microphone and try again.");
        CloseSetup();
        await shell.DisposeAsync();
        await launcher.DisposeAsync();
    });

    // PR 5b: a GUI restarted in the middle of step 7 opens the wizard where it was, and queues nothing.
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

    // PR 5b: over a config the kept walk does not open on its own; Run setup again returns to it.
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

    // Final review I2: Run setup again after Settings edits shows the edits, even when a walk kept at step 3 was resumed
    // before them; the stale walk is gone for good.
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

    private AppPaths Paths() => new(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));

    private static SavedSetup KeptWalk() =>
        new("pl", "whisper-large-v3-turbo-q8", false, "http://localhost:8080/v1", "", null, false, [], false, ["whisper-large-v3-turbo-q8"]);

    private (DesktopShell Shell, FakeLauncher Launcher, TrayControllerTests.FakePresenter Presenter) Create(bool tray = true, bool config = true)
    {
        var paths = new AppPaths(Path.Combine(_dir, "config"), Path.Combine(_dir, "data"));
        var log = new DictumLog(LogLevel.Error, includeText: false, console: null, logsDirectory: null);
        var store = new ConfigStore(paths.ConfigFile, log);
        if (config)
        {
            store.Save(new DictumConfig());
        }

        var socket = Path.Combine(_dir, "d.sock");
        var launcher = new FakeLauncher(socket);
        var presenter = new TrayControllerTests.FakePresenter();
        var shell = new DesktopShell(paths, log, store, socket, launcher, (_, _) => Task.FromResult<ITrayPresenter?>(tray ? presenter : null),
            probeHardware: () => new HardwareInfo("Test GPU", 0, 4L * 1024 * 1024 * 1024));
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
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    public Task TheRailListsThePagesInTheDesignsOrder() => fx.UiAsync(async () =>
    {
        var (shell, launcher, _) = Create();
        shell.Start(Application.Current!, _ => { });

        shell.ShowSettings(SettingsPageKey.Profiles);
        var model = (SettingsViewModel)SettingsWindow.Open[0].DataContext!;

        Assert.Equal(["General", "Profiles", "Speech providers", "Processors", "Models", "System", "History"], model.Pages.Select(p => p.Title));
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
