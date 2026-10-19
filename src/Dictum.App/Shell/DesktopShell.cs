using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Settings;
using Dictum.App.Tray;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.App.Shell;

/// <summary>
/// The composition root of dictum-gui: the daemon supervisor, the tray, the overlay, the config watch, the download
/// queue, the Settings window and the setup wizard, wired by hand. <see cref="Start"/> runs on the UI thread; supervisor
/// and tray events are posted to it.
/// </summary>
public sealed class DesktopShell : ITrayCommands, IAsyncDisposable
{
    private static readonly TimeSpan NoTrayGrace = TimeSpan.FromSeconds(5);

    /// <summary>How often the tray checks whether a held error has expired.</summary>
    private static readonly TimeSpan TrayTickInterval = TimeSpan.FromSeconds(1);

    private readonly AppPaths _paths;
    private readonly DictumLog _log;
    private readonly ConfigStore _store;
    private readonly TrayController _tray;
    private readonly IOverlayDesktop _overlayDesktop;
    private readonly DesktopIntegration _integration;
    private readonly ISoundPlayer _sounds;
    private readonly Func<HardwareInfo> _probeHardware;
    private readonly Lazy<ISecretStore> _secrets;
    private readonly Lazy<ProcessorRunner> _runner;
    private readonly ShellServices _services;
    private readonly SetupCoordinator _setup;
    private readonly StartGate _start = new();
    private SettingsViewModel? _settings;
    private SettingsWindow? _settingsWindow;
    private ConfigWatcher? _watcher;
    private HttpClient? _http;
    private ModelDownloads? _downloads;
    private IModelDownloads? _modelDownloads;
    private Task<HardwareInfo>? _hardware;
    private DispatcherTimer? _tick;
    private Action<int>? _shutdown;
    private volatile bool _highContrast;

    /// <param name="createTray">The OS tray; <see cref="DesktopIntegration.CreateTrayAsync"/> in the app, a fake in tests.</param>
    /// <param name="overlayDesktop">The overlay's OS side; <see cref="OverlayDesktop.None"/> when left out (tests).</param>
    /// <param name="autostart">The OS login item for Settings → General; none when left out, so tests never reach the real one.</param>
    /// <param name="probeHardware">The GPU probe for Settings → Models; the real Vulkan one when left out.</param>
    /// <param name="desktop">The desktop, read once at start; this one when left out.</param>
    public DesktopShell(
        AppPaths paths,
        DictumLog log,
        ConfigStore store,
        string daemonSocket,
        IDaemonLauncher launcher,
        Func<Application, Func<bool>, Task<ITrayPresenter?>> createTray,
        IOverlayDesktop? overlayDesktop = null,
        IAutostart? autostart = null,
        Func<HardwareInfo>? probeHardware = null,
        DesktopEnvironment? desktop = null)
    {
        desktop ??= DesktopEnvironment.Detect();
        _paths = paths;
        _log = log;
        _store = store;
        var logFile = log.FilePath ?? Path.Combine(paths.LogsDirectory, AppPaths.GuiLogName);
        Supervisor = new DaemonSupervisor(daemonSocket, () => store.Exists, launcher, log, logFile);
        Status = new StatusViewModel(store.Path, logFile, Supervisor.Restart);
        _tray = new TrayController(new TrayState(TimeProvider.System), this, TimeProvider.System);
        _overlayDesktop = overlayDesktop ?? OverlayDesktop.None;
        Overlay = new OverlayController(() => new OverlayWindow(_overlayDesktop.Prepare), _overlayDesktop, () => _highContrast, TimeProvider.System, log);
        _integration = new DesktopIntegration(desktop, createTray, log);
        _probeHardware = probeHardware ?? (() => HardwareProbe.Detect(paths));
        _sounds = DictumHost.CreateSoundPlayer(log);
        Frames = new FrameHub(TimeProvider.System);
        _secrets = new Lazy<ISecretStore>(() => PlatformFactory.CreateSecretStore(log));
        _runner = new Lazy<ProcessorRunner>(() => new ProcessorRunner(paths, _secrets.Value, log));
        _services = new ShellServices(
            paths, Catalog.BuiltIn, Supervisor, Status, Frames, UiTimers.Instance, autostart, OwnExecutable(), _sounds,
            () => _secrets.Value, () => _runner.Value, () => _modelDownloads ?? IModelDownloads.None, Hardware, FreeDiskBytes,
            new SpeedMeasurements(), desktop, _integration, wait => Task.Delay(wait), TimeProvider.System, log);
        _setup = new SetupCoordinator(_services, store, Supervisor.Restart, Supervisor.ConfigSaved, () => _settings?.Session.Flush());
    }

    public DaemonSupervisor Supervisor { get; }

    public OverlayController Overlay { get; }

    public StatusViewModel Status { get; }

    /// <summary>The daemon's newest frame, for the Settings meter; the overlay keeps its own.</summary>
    public FrameHub Frames { get; }

    /// <summary>
    /// A later launch asked for Settings, or <c>--quit</c> asked the app to quit (from the single-instance socket, on a
    /// server thread). Before Start there is no shutdown callback and no window yet: Start carries the command out.
    /// </summary>
    public void OnCommand(string command)
    {
        var pending = command switch
        {
            SingleInstance.Quit => PendingCommand.Quit,
            SingleInstance.ShowSettings => PendingCommand.ShowSettings,
            _ => PendingCommand.None,
        };
        if (pending == PendingCommand.None || !_start.Request(pending))
        {
            return;
        }

        if (pending == PendingCommand.Quit)
        {
            Quit();
        }
        else
        {
            Dispatcher.UIThread.Post(() => ShowSettings(SettingsPageKey.General));
        }
    }

    /// <summary>UI thread, from <c>App.OnFrameworkInitializationCompleted</c>.</summary>
    public void Start(Application app, Action<int> shutdown)
    {
        _shutdown = shutdown;
        FollowContrast(app);
        ListenToSupervisor();
        var config = TryLoadConfig();
        _tray.OnConfig(config);
        Overlay.OnConfig(config);
        WatchConfig();
        _tick = new DispatcherTimer { Interval = TrayTickInterval };
        _tick.Tick += (_, _) => _tray.Tick();
        _tick.Start();

        StartDownloads();
        _ = StartTrayAsync(app);
        Supervisor.Start();
        var pending = _start.Open();
        _setup.ResumeKept();

        // No config yet: the wizard opens on its own; ShowSettings sends every Settings entry there too.
        if (pending.HasFlag(PendingCommand.ShowSettings) || !_store.Exists)
        {
            ShowSettings(SettingsPageKey.General);
        }

        if (pending.HasFlag(PendingCommand.Quit))
        {
            Quit();
        }
    }

    private void FollowContrast(Application app)
    {
        _highContrast = IsHighContrast(app);
        if (app.PlatformSettings is { } settings)
        {
            settings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _highContrast = IsHighContrast(app);
                _integration.Tray?.Refresh();
            });
        }
    }

    /// <summary>The supervisor raises its events on its own thread; all but frames go on to the UI thread.</summary>
    private void ListenToSupervisor()
    {
        Supervisor.LinkChanged += link => Dispatcher.UIThread.Post(() =>
        {
            Status.OnLink(link);
            _tray.OnLink(link);
            Overlay.OnLink(link);
            _settings?.OnLink(link);
            _setup.Walk?.OnLink(link);
        });
        Supervisor.EventReceived += message =>
        {
            // Frames come every 20 ms: the overlay keeps the newest one without a trip through the UI thread.
            if (message.Event == IpcEventKind.Frame)
            {
                Overlay.OnFrame(message);
                Frames.Publish(message);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _tray.OnEvent(message);
                Overlay.OnEvent(message);
                _setup.Walk?.OnEvent(message);
            });
        };
    }

    private void WatchConfig()
    {
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
    }

    public void UseProfile(string profile) => Send(new IpcRequest(IpcCommand.UseProfile, profile));

    public void PauseHotkeys() => Send(new IpcRequest(IpcCommand.PauseHotkeys));

    public void ResumeHotkeys() => Send(new IpcRequest(IpcCommand.ResumeHotkeys));

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
        var settings = new SettingsViewModel(session, SettingsPages.Create(session, _services, new WindowClipboard(() => _settingsWindow), ShowSetup));
        var window = new SettingsWindow { DataContext = settings };
        window.Closed += (_, _) =>
        {
            _settings = null;
            _settingsWindow = null;
            settings.Dispose();
        };
        _settings = settings;
        _settingsWindow = window;
        settings.Open(page);
        window.Show();
    }

    /// <summary>
    /// Opens the setup wizard, or brings it forward. One walk lives as long as the app: a closed wizard reopens
    /// at the same step with the same answers, and Finish ends the walk.
    /// </summary>
    public void ShowSetup() => _setup.Show();

    /// <summary>The one GPU probe, run once off the UI thread; Settings → Models and the wizard share it.</summary>
    private Task<HardwareInfo> Hardware() => _hardware ??= Task.Run(_probeHardware);

    private long FreeDiskBytes() => HardwareProbe.FreeDiskBytes(_paths);

    /// <summary>What a login item starts: the AppImage when Dictum runs from one, this executable otherwise.</summary>
    private static string? OwnExecutable() => InstallLocation.AppImage ?? Environment.ProcessPath;

    public void Quit() => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            await Supervisor.QuitAsync();
        }
        catch (Exception ex)
        {
            // Quit ends the app even when the daemon could not be stopped.
            _log.Error($"Quit: {ex.Message}", ex);
        }

        _shutdown?.Invoke(0);
    });

    public async ValueTask DisposeAsync()
    {
        _tick?.Stop();
        _watcher?.Dispose();
        _settings?.Dispose();
        (_overlayDesktop as IDisposable)?.Dispose();
        await _integration.DisposeAsync().ConfigureAwait(false);
        if (_downloads is not null)
        {
            await _downloads.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        _setup.Dispose();
        if (_runner.IsValueCreated)
        {
            _runner.Value.Dispose();
        }

        (_sounds as IDisposable)?.Dispose();
        await Supervisor.DisposeAsync().ConfigureAwait(false);
    }

    internal static bool IsHighContrast(Application app) =>
        app.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    private async Task StartTrayAsync(Application app)
    {
        try
        {
            // Stays on the UI thread: the presenter is attached and shown from here.
            var presenter = await _integration.StartTrayAsync(app, () => _highContrast);
            if (presenter is null)
            {
                NoTray();
                return;
            }

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
            // Without a tray the app goes on: the System page says so and stands in for it.
            _log.Error($"The tray icon could not start: {ex.Message}", ex);
            NoTray();
        }
    }

    private void NoTray()
    {
        Status.NoTray = true;
        ShowSettings(SettingsPageKey.System);
    }

    /// <summary>Downloads a previous run left unfinished continue in the background; the tooltip shows the total.</summary>
    private void StartDownloads()
    {
        _http = FileDownloader.CreateHttpClient(DictumExecutables.DesktopApp);
        _downloads = new ModelDownloads(new FileDownloader(_http), _paths, Catalog.BuiltIn, FreeDiskBytes);
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
        catch (Exception ex) when (IpcFailure.IsTransport(ex, CancellationToken.None))
        {
            _log.Warn($"'{request.Command}' not sent: {ex.Message}");
        }
        catch (Exception ex) when (IpcFailure.IsBadReply(ex))
        {
            // Nobody awaits a tray request, so an answer that cannot be read ends here.
            _log.Warn($"'{request.Command}' got an answer that could not be read: {ex.Message}");
        }
    }
}
