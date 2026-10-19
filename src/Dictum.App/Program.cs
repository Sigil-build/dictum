using System.Text;
using Avalonia;
using Avalonia.Controls;
using Dictum.App.Daemon;
using Dictum.App.Overlay;
using Dictum.App.Shell;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Host.Ipc;
using Dictum.Platform.Linux.X11;

namespace Dictum.App;

internal static class Program
{
    /// <summary>The exit code for arguments that cannot be used, as a command line tool gives.</summary>
    private const int UsageExitCode = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            // UTF-8, so the Ukrainian lines stay readable in CI logs; a GUI exe has no console code page to follow.
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            return SelfTest.Run(output);
        }

        if (args.Contains("--quit"))
        {
            return QuitRunning();
        }

        if (OperatingSystem.IsLinux())
        {
            // Avalonia's X11 backend reports scale 1 where KDE sets the scale through Xft.dpi.
            DisplayScale.Apply();
        }

        if (args.Contains(OverlayDemo.Flag))
        {
            return RunOverlayDemo(args);
        }

        var paths = AppPaths.FromEnvironment();
        using var log = OpenLog(paths);
        var store = new ConfigStore(paths.ConfigFileFrom(explicitPath: null), log);
        UiCulture.Apply(UiCulture.FromConfig(store));

        var daemonSocket = IpcEndpoint.SocketPath(paths);
        var appSocket = SingleInstance.SocketPathFor(daemonSocket);
        var shell = new DesktopShell(paths, log, store, daemonSocket, DaemonProcessLauncher.NextToApp(log),
            (app, highContrast) => DesktopIntegration.CreateTrayAsync(app, highContrast, paths, log),
            OverlayDesktop.ForThisDesktop(log), PlatformFactory.CreateAutostart());

        // No synchronisation context exists yet, so blocking here cannot deadlock.
        var instance = SingleInstance.AcquireAsync(appSocket, shell.OnCommand, log).GetAwaiter().GetResult();
        if (instance is null)
        {
            try
            {
                SingleInstance.NotifyAsync(appSocket, SingleInstance.ShowSettings, CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }
            catch (DaemonNotRunningException ex)
            {
                log.Error($"Another dictum-gui holds the lock but does not answer: {ex.Message}");
                return 1;
            }
        }

        try
        {
            App.Shell = shell;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            // Every await inside these disposals uses ConfigureAwait(false), so nothing waits for the stopped dispatcher.
            shell.DisposeAsync().AsTask().GetAwaiter().GetResult();
            instance.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// <c>--quit</c>: stops a running Dictum (the shell's Quit also stops the daemon) for the install and uninstall
    /// scripts. 0 when none runs or it is gone, 1 when it is still running.
    /// </summary>
    private static int QuitRunning()
    {
        var daemonSocket = IpcEndpoint.SocketPath(AppPaths.FromEnvironment());
        return SingleInstance.QuitCommandAsync(SingleInstance.SocketPathFor(daemonSocket), daemonSocket, Console.Error, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary><c>--overlay-demo</c>: no daemon, no tray, no single-instance lock. Messages stay English, like --self-test.</summary>
    private static int RunOverlayDemo(string[] args)
    {
        if (OverlayDemo.Parse(args, Console.Error) is not { } options)
        {
            return UsageExitCode;
        }

        FrameRecording recording;
        try
        {
            recording = FrameRecording.Load(options.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine($"Cannot play {options.Path}: {ex.Message}");
            return UsageExitCode;
        }

        var paths = AppPaths.FromEnvironment();
        using var log = OpenLog(paths);
        var store = new ConfigStore(paths.ConfigFileFrom(explicitPath: null), log);
        UiCulture.Apply(UiCulture.FromConfig(store));
        DictumConfig? user = null;
        try
        {
            user = store.Exists ? store.Load() : null;
        }
        catch (Exception ex) when (ex is ConfigException or IOException)
        {
            // An unreadable config: the demo shows the default look.
        }

        using var desktop = OverlayDesktop.ForThisDesktop(log);
        App.Demo = exit =>
        {
            var controller = new OverlayController(
                () => new OverlayWindow(desktop.Prepare),
                desktop,
                () => Application.Current is { } app && DesktopShell.IsHighContrast(app),
                TimeProvider.System,
                log);
            controller.OnConfig(OverlayDemo.Config(user));
            new OverlayDemo(controller, recording, options.State, TimeProvider.System).Start(exit);
        };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static DictumLog OpenLog(AppPaths paths) =>
        new(LogLevel.Warning, includeText: false, console: null, paths.LogsDirectory, AppPaths.GuiLogName);

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
