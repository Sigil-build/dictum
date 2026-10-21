using Avalonia.Threading;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Setup;
using Dictum.Core.Configuration;
using Dictum.Core.Http;

namespace Dictum.App.Shell;

/// <summary>
/// The setup wizard's lifecycle. One walk lives as long as the app: a closed wizard reopens at the same step with the
/// same answers, and Finish ends the walk. A walk kept on disk mid-download comes back when its files are in.
/// </summary>
/// <param name="beforeShow">Runs before the wizard opens: an open Settings window saves its pending edits.</param>
/// <param name="configSaved">The wizard wrote the config: the daemon starts at once, not at the supervisor's next poll.</param>
internal sealed class SetupCoordinator(
    ShellServices services, ConfigStore store, Action restartDaemon, Action configSaved, Action beforeShow) : IDisposable
{
    private SetupWindow? _window;
    private HttpClient? _serverHttp;

    /// <summary>The walk in progress, open or closed; null when none.</summary>
    public SetupViewModel? Walk { get; private set; }

    /// <summary>
    /// At start, over a config: a walk kept on disk mid-download waits in the background and comes back when its files
    /// are in, or when the setup is opened again. Without a config the wizard opens anyway.
    /// </summary>
    public void ResumeKept()
    {
        if (store.Exists && File.Exists(services.Paths.SetupFile))
        {
            Walk = Create();
            Walk.OnClosed();
        }
    }

    /// <summary>Opens the wizard, or brings it forward.</summary>
    public void Show()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Show);
            return;
        }

        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        // An open Settings window reloads from the file the wizard writes.
        beforeShow();
        if (Walk is { CanResume: false })
        {
            Walk = null;
        }

        var walk = Walk ??= Create();
        var window = new SetupWindow { DataContext = walk };
        window.Closed += (_, _) =>
        {
            _window = null;
            walk.OnClosed();
        };
        _window = window;
        window.Show();
        walk.OnOpened();
    }

    /// <summary>A new walk over the config on disk and the walk kept on disk, if any.</summary>
    public SetupViewModel Create()
    {
        var secrets = services.Secrets();
        var walk = new SetupViewModel(new SetupServices(
            services.Paths, store, services.Catalog, services.Downloads(), services.Hardware, services.FreeDiskBytes,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount, services.Measurements,
            new ServerProbe(_serverHttp ??= ProviderHttp.CreateClient(), secrets, services.Time), secrets, services.Autostart, services.Executable,
            services.Integration.SetupTray(), services.Daemon, restartDaemon, services.OpenDocs, services.Timers, services.Time, services.Log),
            SavedSetupFile.Load(services.Paths, services.Log));
        walk.Finished += () =>
        {
            Walk = null;
            _window?.Close();
        };

        // Closed on step 7: when the files are in, the walk comes back to save.
        walk.FilesReady += () =>
        {
            if (ReferenceEquals(Walk, walk))
            {
                Show();
            }
        };

        walk.Saved += configSaved;
        return walk;
    }

    public void Dispose() => _serverHttp?.Dispose();
}
