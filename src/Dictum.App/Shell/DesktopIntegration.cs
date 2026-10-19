using System.Runtime.InteropServices;
using Avalonia;
using Dictum.App.Settings;
using Dictum.App.Tray;
using Dictum.App.ViewModels.Setup;
using Dictum.Core;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux;

namespace Dictum.App.Shell;

/// <summary>
/// dictum-gui on this desktop: the tray icon for the OS, and what the System page and the wizard say about the tray, the
/// overlay and the desktop.
/// </summary>
/// <param name="createTray">The OS tray; <see cref="CreateTrayAsync"/> in the app, a fake in tests.</param>
internal sealed class DesktopIntegration(Func<Application, Func<bool>, Task<ITrayPresenter?>> createTray, ILog log) : IAsyncDisposable
{
    /// <summary>The tray icon once it started; null before, or when this desktop has none.</summary>
    public ITrayPresenter? Tray { get; private set; }

    /// <summary>The desktop as the System page names it: the session on Linux, the OS elsewhere.</summary>
    public string Name { get; } = OperatingSystem.IsLinux() ? SessionInfo.Detect().ToString() : RuntimeInformation.OSDescription;

    /// <summary>The tray for this OS: Avalonia's on Windows, our StatusNotifierItem on Linux, none elsewhere.</summary>
    public static async Task<ITrayPresenter?> CreateTrayAsync(Application app, Func<bool> highContrast, AppPaths paths, ILog log)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsTrayPresenter(app, highContrast);
        }

        if (OperatingSystem.IsLinux())
        {
            return await SniTrayPresenter.TryStartAsync(paths.TrayIconsDirectory, highContrast, log);
        }

        return null;
    }

    /// <summary>Starts the tray icon on the UI thread; null when this desktop has none.</summary>
    public async Task<ITrayPresenter?> StartTrayAsync(Application app, Func<bool> highContrast) => Tray = await createTray(app, highContrast);

    public static SystemRow OverlayRow(bool enabled) =>
        OverlayAvailability.Row(OverlayAvailability.ThisDesktop(Environment.GetEnvironmentVariable), enabled);

    public SystemRow TrayRow()
    {
        var desktop = TrayVisibility.ThisDesktop();
        var available = Tray?.IsAvailable == true;
        var hidden = available ? TrayVisibility.Hidden(desktop, Environment.ProcessPath, log) : null;
        return TrayVisibility.Row(desktop, available, hidden, OperatingSystem.IsWindows() ? TrayVisibility.OpenTaskbarSettings : null);
    }

    /// <summary>What the wizard's last step says about the tray icon.</summary>
    public TrayHint SetupTray()
    {
        var desktop = TrayVisibility.ThisDesktop();
        return new TrayHint(
            desktop,
            () => Tray?.IsAvailable == true ? TrayVisibility.Hidden(desktop, Environment.ProcessPath, log) : null,
            desktop == TrayDesktop.Windows ? TrayVisibility.OpenTaskbarSettings : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Tray is not null)
        {
            await Tray.DisposeAsync().ConfigureAwait(false);
        }
    }
}
