using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using Dictum.App.Daemon;
using Dictum.App.Tray;
using Dictum.App.ViewModels;
using Dictum.App.Views;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Daemon;
using Dictum.Host.Ipc;
using Dictum.Setup;

namespace Dictum.App.Shell;

/// <summary>
/// The composition root of dictum-gui: the daemon supervisor, the tray, the config watch, the download queue and the
/// status window, wired by hand. <see cref="Start"/> runs on the UI thread; supervisor and tray events are posted to it.
/// </summary>
public sealed class DesktopShell : ITrayCommands, IAsyncDisposable
{
    private static readonly TimeSpan NoTrayGrace = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly DictumLog _log;
    private readonly ConfigStore _store;
    private readonly Func<Application, Func<bool>, Task<ITrayPresenter?>> _createTray;
    private readonly TrayController _tray;
    private ITrayPresenter? _presenter;
    private ConfigWatcher? _watcher;
    private HttpClient? _http;
    private DownloadQueue? _downloads;
    private DispatcherTimer? _tick;
    private Action<int>? _shutdown;
    private volatile bool _highContrast;
    private volatile bool _started;
    private volatile bool _showWhenStarted;

    /// <param name="createTray">The OS tray; <see cref="CreateTrayAsync"/> in the app, a fake in tests.</param>
    public DesktopShell(
        AppPaths paths,
        DictumLog log,
        ConfigStore store,
        string daemonSocket,
        IDaemonLauncher launcher,
        Func<Application, Func<bool>, Task<ITrayPresenter?>> createTray)
    {
        _paths = paths;
        _log = log;
        _store = store;
        _createTray = createTray;
        var logFile = log.FilePath ?? Path.Combine(paths.LogsDirectory, "dictum-gui.log");
        Supervisor = new DaemonSupervisor(daemonSocket, () => store.Exists, launcher, log, logFile);
        Status = new StatusViewModel(store.Path, logFile, Supervisor.Restart);
        _tray = new TrayController(new TrayState(TimeProvider.System), this, TimeProvider.System);
    }

    public DaemonSupervisor Supervisor { get; }

    public StatusViewModel Status { get; }

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
        if (command != SingleInstance.ShowSettings)
        {
            return;
        }

        if (_started)
        {
            Dispatcher.UIThread.Post(ShowStatus);
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
        });
        Supervisor.EventReceived += message =>
        {
            if (message.Event != "frame")
            {
                Dispatcher.UIThread.Post(() => _tray.OnEvent(message));
            }
        };

        _tray.OnConfig(TryLoadConfig());
        _watcher = new ConfigWatcher(
            _store,
            (config, _) =>
            {
                Dispatcher.UIThread.Post(() => _tray.OnConfig(config));
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _log);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _tray.Tick();
        _tick.Start();

        StartDownloads();
        _ = StartTrayAsync(app);
        Supervisor.Start();
        _started = true;
        if (_showWhenStarted)
        {
            ShowStatus();
        }
    }

    public void UseProfile(string profile) => Send(new IpcRequest("use-profile", profile));

    public void PauseHotkeys() => Send(new IpcRequest("pause-hotkeys"));

    public void ResumeHotkeys() => Send(new IpcRequest("resume-hotkeys"));

    public void RestartDaemon() => Supervisor.Restart();

    public void ShowStatus()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ShowStatus);
            return;
        }

        if (StatusWindow.Open.FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }

        new StatusWindow { DataContext = Status }.Show();
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
        if (_presenter is not null)
        {
            await _presenter.DisposeAsync().ConfigureAwait(false);
        }

        if (_downloads is not null)
        {
            await _downloads.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        await Supervisor.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsHighContrast(Application app) =>
        app.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

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
        ShowStatus();
    }

    /// <summary>Downloads a previous run left unfinished continue in the background (spec 9); the tooltip shows the total.</summary>
    private void StartDownloads()
    {
        _http = FileDownloader.CreateHttpClient("dictum-gui");
        _downloads = new DownloadQueue(new FileDownloader(_http), _paths, Catalog.BuiltIn, () => HardwareProbe.FreeDiskBytes(_paths));
        var queue = _downloads;
        queue.Changed += _ => Dispatcher.UIThread.Post(() => _tray.OnDownloads(QueueProgress.Of(queue.Items)));
        try
        {
            queue.Restore();
        }
        catch (IOException ex)
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
