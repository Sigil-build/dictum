using System.Text;
using Avalonia;
using Avalonia.Controls;
using Dictum.App.Daemon;
using Dictum.App.Shell;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform.Linux.X11;

namespace Dictum.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            // UTF-8, so the Ukrainian lines stay readable in CI logs; a GUI exe has no console code page to follow.
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            return SelfTest.Run(output);
        }

        if (OperatingSystem.IsLinux())
        {
            // Spike: Avalonia's X11 backend reported scale 1 where KDE signals the scale through Xft.dpi.
            DisplayScale.Apply();
        }

        var paths = AppPaths.FromEnvironment();
        using var log = new DictumLog(LogLevel.Warning, includeText: false, console: null, paths.LogsDirectory, "dictum-gui.log");
        var configPath = Environment.GetEnvironmentVariable("DICTUM_CONFIG") is { Length: > 0 } custom ? custom : paths.ConfigFile;
        var store = new ConfigStore(configPath, log);
        UiCulture.Apply(UiCulture.FromConfig(store));

        var daemonSocket = IpcEndpoint.SocketPath(paths);
        var appSocket = SingleInstance.SocketPathFor(daemonSocket);
        var shell = new DesktopShell(paths, log, store, daemonSocket, DaemonProcessLauncher.NextToApp(log),
            (app, highContrast) => DesktopShell.CreateTrayAsync(app, highContrast, paths, log));

        // No synchronisation context exists yet, so blocking here cannot deadlock.
        var instance = SingleInstance.AcquireAsync(appSocket, shell.OnCommand, log, CancellationToken.None).GetAwaiter().GetResult();
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

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
