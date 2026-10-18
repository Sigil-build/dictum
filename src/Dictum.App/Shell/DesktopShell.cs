using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Settings;
using Dictum.App.Tray;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Settings;
using Dictum.App.Views.Setup;
using Dictum.Audio.PortAudio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Platform.Linux;
using Dictum.Setup;

namespace Dictum.App.Shell;

/// <summary>
/// The composition root of dictum-gui: the daemon supervisor, the tray, the overlay, the config watch, the download
/// queue and the Settings window, wired by hand. <see cref="Start"/> runs on the UI thread; supervisor and tray events are posted to it.
/// </summary>
public sealed class DesktopShell : ITrayCommands, IAsyncDisposable
{
    private static readonly TimeSpan NoTrayGrace = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly DictumLog _log;
    private readonly ConfigStore _store;
    private readonly Func<Application, Func<bool>, Task<ITrayPresenter?>> _createTray;
    private readonly TrayController _tray;
    private readonly IOverlayDesktop _overlayDesktop;
    private readonly IAutostart? _autostart;
    private readonly SoundPlayer _sounds;
    private readonly Func<HardwareInfo> _probeHardware;
    private readonly SpeedMeasurements _measurements = new();
    private SettingsViewModel? _settings;
    private SettingsWindow? _settingsWindow;
    private ISecretStore? _secrets;
    private ProcessorRunner? _runner;
    private ITrayPresenter? _presenter;
    private ConfigWatcher? _watcher;
    private HttpClient? _http;
    private DownloadQueue? _downloads;
    private IModelDownloads? _modelDownloads;
    private Task<HardwareInfo>? _hardware;
    private SetupViewModel? _setup;
    private SetupWindow? _setupWindow;
    private HttpClient? _serverHttp;
    private DispatcherTimer? _tick;
    private Action<int>? _shutdown;
    private volatile bool _highContrast;
    private volatile bool _started;
    private volatile bool _showWhenStarted;
    private volatile bool _quitWhenStarted;

    /// <param name="createTray">The OS tray; <see cref="CreateTrayAsync"/> in the app, a fake in tests.</param>
    /// <param name="overlayDesktop">The overlay's OS side; <see cref="OverlayDesktop.None"/> when left out (tests).</param>
    /// <param name="autostart">The OS login item for Settings → General; none when left out, so tests never reach the real one.</param>
    /// <param name="probeHardware">The GPU probe for Settings → Models; the real Vulkan one when left out.</param>
    public DesktopShell(
        AppPaths paths,
        DictumLog log,
        ConfigStore store,
        string daemonSocket,
        IDaemonLauncher launcher,
        Func<Application, Func<bool>, Task<ITrayPresenter?>> createTray,
        IOverlayDesktop? overlayDesktop = null,
        IAutostart? autostart = null,
        Func<HardwareInfo>? probeHardware = null)
    {
        _paths = paths;
        _log = log;
        _store = store;
        _createTray = createTray;
        var logFile = log.FilePath ?? Path.Combine(paths.LogsDirectory, "dictum-gui.log");
        Supervisor = new DaemonSupervisor(daemonSocket, () => store.Exists, launcher, log, logFile);
        Status = new StatusViewModel(store.Path, logFile, Supervisor.Restart);
        _tray = new TrayController(new TrayState(TimeProvider.System), this, TimeProvider.System);
        _overlayDesktop = overlayDesktop ?? OverlayDesktop.None;
        Overlay = new OverlayController(() => new OverlayWindow(_overlayDesktop.Prepare), _overlayDesktop, () => _highContrast, TimeProvider.System, log);
        _autostart = autostart;
        _probeHardware = probeHardware ?? (() => HardwareProbe.Detect(paths));
        _sounds = new SoundPlayer(log);
        Frames = new FrameHub(TimeProvider.System);
    }

    public DaemonSupervisor Supervisor { get; }

    public OverlayController Overlay { get; }

    public StatusViewModel Status { get; }

    /// <summary>The daemon's newest frame, for the Settings meter; the overlay keeps its own.</summary>
    public FrameHub Frames { get; }

    /// <summary>The tray for this OS: Avalonia's on Windows, our StatusNotifierItem on Linux, none elsewhere.</summary>
    public static async Task<ITrayPresenter?> CreateTrayAsync(Application app, Func<bool> highContrast, AppPaths paths, ILog log)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsTrayPresenter(app, highContrast);
        }

        if (OperatingSystem.IsLinux())
        {
            return await SniTrayPresenter.TryStartAsync(Path.Combine(paths.DataDirectory, "tray-icons"), highContrast, log);
        }

        return null;
    }

    /// <summary>A later launch asked for Settings (from the single-instance socket, on a server thread).</summary>
    public void OnCommand(string command)
    {
        if (command == SingleInstance.Quit)
        {
            // Before Start there is no shutdown callback yet: quit as soon as it exists (phase 4 --quit).
            if (_started)
            {
                Quit();
            }
            else
            {
                _quitWhenStarted = true;
            }

            return;
        }

        if (command != SingleInstance.ShowSettings)
        {
            return;
        }

        if (_started)
        {
            Dispatcher.UIThread.Post(() => ShowSettings(SettingsPageKey.General));
        }
        else
        {
            _showWhenStarted = true;
        }
    }

    /// <summary>UI thread, from <c>App.OnFrameworkInitializationCompleted</c>.</summary>
    public void Start(Application app, Action<int> shutdown)
    {
        _shutdown = shutdown;
        _highContrast = IsHighContrast(app);
        if (app.PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _highContrast = IsHighContrast(app);
                _presenter?.Refresh();
            });
        }

        Supervisor.LinkChanged += link => Dispatcher.UIThread.Post(() =>
        {
            Status.OnLink(link);
            _tray.OnLink(link);
            Overlay.OnLink(link);
            _settings?.OnLink(link);
            _setup?.OnLink(link);
        });
        Supervisor.EventReceived += message =>
        {
            // Frames come every 20 ms: the overlay keeps the newest one without a trip through the UI thread.
            if (message.Event == "frame")
            {
                Overlay.OnFrame(message);
                Frames.Publish(message);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _tray.OnEvent(message);
                Overlay.OnEvent(message);
                _setup?.OnEvent(message);
            });
        };

        var config = TryLoadConfig();
        _tray.OnConfig(config);
        Overlay.OnConfig(config);
        try
        {
            _watcher = new ConfigWatcher(
                _store,
                (changed, _) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _tray.OnConfig(changed);
                        Overlay.OnConfig(changed);
                        _settings?.Session.OnFileChanged(changed);
                    });
                    return Task.CompletedTask;
                },
                invalid =>
                {
                    Dispatcher.UIThread.Post(() => _settings?.Session.OnFileInvalid(invalid));
                    return Task.CompletedTask;
                },
                _log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Linux can run out of inotify instances; the tray then misses config edits until the next start.
            _log.Warn($"Not watching the config for changes: {ex.Message}");
        }

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _tray.Tick();
        _tick.Start();

        StartDownloads();
        _ = StartTrayAsync(app);
        Supervisor.Start();
        _started = true;

        // A walk kept on disk mid-download (PR 5b): with no config it opens below; over a config it waits in the
        // background and comes back when its files are in (FilesReady), or when the user runs the setup again.
        if (_store.Exists && File.Exists(_paths.SetupFile))
        {
            _setup = CreateSetup();
            _setup.OnClosed();
        }

        // No config yet: the wizard opens on its own (spec 3.2); ShowSettings sends every Settings entry there too.
        if (_showWhenStarted || !_store.Exists)
        {
            ShowSettings(SettingsPageKey.General);
        }

        if (_quitWhenStarted)
        {
            Quit();
        }
    }

    public void UseProfile(string profile) => Send(new IpcRequest("use-profile", profile));

    public void PauseHotkeys() => Send(new IpcRequest("pause-hotkeys"));

    public void ResumeHotkeys() => Send(new IpcRequest("resume-hotkeys"));

    public void RestartDaemon() => Supervisor.Restart();

    public void ShowSettings() => ShowSettings(SettingsPageKey.General);

    public void ShowHistory() => ShowSettings(SettingsPageKey.History);

    /// <summary>Opens the Settings window at <paramref name="page"/>, or brings the open one forward there.</summary>
    public void ShowSettings(SettingsPageKey page)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowSettings(page));
            return;
        }

        if (!_store.Exists)
        {
            ShowSetup();
            return;
        }

        if (_settings is not null && _settingsWindow is not null)
        {
            _settings.Open(page);
            _settingsWindow.Activate();
            return;
        }

        var session = new SettingsSession(_store, UiTimers.Instance, _log);
        session.Load();
        var general = new GeneralPage(session, new GeneralServices(
            Supervisor, Frames, UiTimers.Instance, _autostart, InstallLocation.AppImage ?? Environment.ProcessPath, _sounds, TimeProvider.System, _log));
        general.SetupRequested += ShowSetup;
        var profiles = new ProfilesPage(session, Supervisor, _log);
        var providers = new SpeechProvidersPage(session, new SpeechServices(
            Supervisor, Frames, UiTimers.Instance, _secrets ??= PlatformFactory.CreateSecretStore(_log),
            current => WhisperModels.Choices(_paths, Catalog.BuiltIn, current), model => WhisperModels.IsPresent(_paths, model),
            wait => Task.Delay(wait), TimeProvider.System, _log));
        var system = new SystemPage(new SystemServices(
            Supervisor, Status, () => OverlayRow(session), TrayRow, UiTimers.Instance, TimeProvider.System, DesktopName()));
        var runner = _runner ??= new ProcessorRunner(_paths, _secrets ??= PlatformFactory.CreateSecretStore(_log), _log);
        var processors = new ProcessorsPage(session, new ProcessorsServices(runner, _secrets, new PromptFiles(_paths.PromptsDirectory), UiTimers.Instance, _log));
        var models = new ModelsPage(session, new ModelsServices(
            _paths, Catalog.BuiltIn, _modelDownloads ?? ModelDownloads.None,
            Hardware, () => HardwareProbe.FreeDiskBytes(_paths),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount, _measurements, Supervisor, UiTimers.Instance,
            wait => Task.Delay(wait), TimeProvider.System, _log));
        var history = new HistoryPage(session, _paths.HistoryFile, new WindowClipboard(() => _settingsWindow), runner, _log);
        var settings = new SettingsViewModel(session,
        [
            (SettingsPageKey.General, general),
            (SettingsPageKey.Profiles, profiles),
            (SettingsPageKey.SpeechProviders, providers),
            (SettingsPageKey.Processors, processors),
            (SettingsPageKey.Models, models),
            (SettingsPageKey.System, system),
            (SettingsPageKey.History, history),
        ]);
        var window = new SettingsWindow { DataContext = settings };
        window.Closed += (_, _) =>
        {
            _settings = null;
            _settingsWindow = null;
        };
        _settings = settings;
        _settingsWindow = window;
        settings.Open(page);
        window.Show();
    }

    /// <summary>
    /// Opens the setup wizard (spec 8), or brings it forward. One walk lives as long as the app: a closed wizard reopens
    /// at the same step with the same answers, and Finish ends the walk.
    /// </summary>
    public void ShowSetup()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowSetup);
            return;
        }

        if (_setupWindow is not null)
        {
            _setupWindow.Activate();
            return;
        }

        // An open Settings window reloads from the file the wizard writes; its pending edits are saved first.
        _settings?.Session.Flush();
        if (_setup is { CanResume: false })
        {
            _setup = null;
        }

        var setup = _setup ??= CreateSetup();
        var window = new SetupWindow { DataContext = setup };
        window.Closed += (_, _) =>
        {
            _setupWindow = null;
            setup.OnClosed();
        };
        _setupWindow = window;
        window.Show();
        setup.OnOpened();
    }

    private SetupViewModel CreateSetup()
    {
        var secrets = _secrets ??= PlatformFactory.CreateSecretStore(_log);
        var setup = new SetupViewModel(new SetupServices(
            _paths, _store, Catalog.BuiltIn, _modelDownloads ?? ModelDownloads.None, Hardware, () => HardwareProbe.FreeDiskBytes(_paths),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount, _measurements,
            new ServerProbe(_serverHttp ??= new HttpClient(), secrets, TimeProvider.System), secrets, _autostart, InstallLocation.AppImage ?? Environment.ProcessPath,
            SetupTray(), Supervisor, Supervisor.Restart, UiTimers.Instance, TimeProvider.System, _log),
            SavedSetupFile.Load(_paths, _log));
        setup.Finished += () =>
        {
            _setup = null;
            _setupWindow?.Close();
        };

        // Closed on step 7: when the files are in, the walk comes back to save (final review M6).
        setup.FilesReady += () =>
        {
            if (ReferenceEquals(_setup, setup))
            {
                ShowSetup();
            }
        };

        // The daemon starts at once after the save, not at the supervisor's next poll (PR 5b).
        setup.Saved += Supervisor.ConfigSaved;
        return setup;
    }

    /// <summary>The one GPU probe, run once off the UI thread; Settings → Models and the wizard share it (SPEC:147).</summary>
    private Task<HardwareInfo> Hardware() => _hardware ??= Task.Run(_probeHardware);

    private TrayHint SetupTray()
    {
        var desktop = TrayVisibility.ThisDesktop();
        return new TrayHint(
            desktop,
            () => _presenter?.IsAvailable == true ? TrayVisibility.Hidden(desktop, Environment.ProcessPath, _log) : null,
            desktop == TrayDesktop.Windows ? TrayVisibility.OpenTaskbarSettings : null);
    }

    public void Quit() => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            await Supervisor.QuitAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"Quit: {ex.Message}", ex);
        }

        _shutdown?.Invoke(0);
    });

    public async ValueTask DisposeAsync()
    {
        _tick?.Stop();
        _watcher?.Dispose();
        (_overlayDesktop as IDisposable)?.Dispose();
        if (_presenter is not null)
        {
            await _presenter.DisposeAsync().ConfigureAwait(false);
        }

        if (_downloads is not null)
        {
            await _downloads.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        _serverHttp?.Dispose();
        _runner?.Dispose();
        await Supervisor.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsHighContrast(Application app) =>
        app.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    private static SystemRow OverlayRow(SettingsSession session) =>
        OverlayAvailability.Row(OverlayAvailability.ThisDesktop(Environment.GetEnvironmentVariable), session.Config.General.Overlay.Enabled);

    private SystemRow TrayRow()
    {
        var desktop = TrayVisibility.ThisDesktop();
        var available = _presenter?.IsAvailable == true;
        var hidden = available ? TrayVisibility.Hidden(desktop, Environment.ProcessPath, _log) : null;
        return TrayVisibility.Row(desktop, available, hidden, OperatingSystem.IsWindows() ? TrayVisibility.OpenTaskbarSettings : null);
    }

    private static string DesktopName() => OperatingSystem.IsLinux() ? SessionInfo.Detect().ToString() : RuntimeInformation.OSDescription;

    private async Task StartTrayAsync(Application app)
    {
        try
        {
            // Stays on the UI thread: the presenter is attached and shown from here.
            var presenter = await _createTray(app, () => _highContrast);
            if (presenter is null)
            {
                NoTray();
                return;
            }

            _presenter = presenter;
            presenter.AvailabilityChanged += () => Dispatcher.UIThread.Post(() =>
            {
                if (presenter.IsAvailable)
                {
                    Status.NoTray = false;
                    return;
                }

                // At login the panel can take a moment to start its tray host.
                DispatcherTimer.RunOnce(() =>
                {
                    if (!presenter.IsAvailable)
                    {
                        NoTray();
                    }
                }, NoTrayGrace);
            });
            _tray.Attach(presenter);
        }
        catch (Exception ex)
        {
            _log.Error($"The tray icon could not start: {ex.Message}", ex);
            NoTray();
        }
    }

    private void NoTray()
    {
        Status.NoTray = true;
        ShowSettings(SettingsPageKey.System);
    }

    /// <summary>Downloads a previous run left unfinished continue in the background (spec 9); the tooltip shows the total.</summary>
    private void StartDownloads()
    {
        _http = FileDownloader.CreateHttpClient("dictum-gui");
        _downloads = new DownloadQueue(new FileDownloader(_http), _paths, Catalog.BuiltIn, () => HardwareProbe.FreeDiskBytes(_paths));
        var queue = _downloads;
        _modelDownloads = new QueueDownloads(queue, action => Dispatcher.UIThread.Post(action));
        queue.Changed += _ => Dispatcher.UIThread.Post(() => _tray.OnDownloads(QueueProgress.Of(queue.Items)));
        try
        {
            queue.Restore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not restore the download queue: {ex.Message}");
        }
    }

    private DictumConfig? TryLoadConfig()
    {
        try
        {
            return _store.Exists ? _store.Load() : null;
        }
        catch (Exception ex) when (ex is ConfigException or IOException)
        {
            _log.Warn($"Config not read: {ex.Message}");
            return null;
        }
    }

    private void Send(IpcRequest request) => _ = SendAsync(request);

    private async Task SendAsync(IpcRequest request)
    {
        try
        {
            var answer = await Supervisor.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (!answer.Ok)
            {
                _log.Warn($"'{request.Command}' refused: {answer.Error}");
            }
        }
        catch (Exception ex) when (ex is DaemonNotRunningException or IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            _log.Warn($"'{request.Command}' not sent: {ex.Message}");
        }
    }
}
