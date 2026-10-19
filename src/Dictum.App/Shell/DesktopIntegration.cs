using Avalonia;
using Dictum.App.Settings;
using Dictum.App.Tray;
using Dictum.App.ViewModels.Setup;
using Dictum.Core;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Shell;

/// <summary>
/// dictum-gui on this desktop: the tray icon for the OS, and what the System page and the wizard say about the tray, the
/// overlay and the desktop.
/// </summary>
/// <param name="createTray">The OS tray; <see cref="CreateTrayAsync"/> in the app, a fake in tests.</param>
internal sealed class DesktopIntegration(DesktopEnvironment desktop, Func<Application, Func<bool>, Task<ITrayPresenter?>> createTray, ILog log)
    : IAsyncDisposable
{
    /// <summary>The tray icon once it started; null before, or when this desktop has none.</summary>
    public ITrayPresenter? Tray { get; private set; }

    /// <inheritdoc cref="DesktopEnvironment.Name"/>
    public string Name => desktop.Name;

    /// <summary>The OS page that shows a hidden tray icon: Windows' taskbar settings; none elsewhere.</summary>
    private Action? OpenTaskbarSettings => desktop.Tray == TrayDesktop.Windows ? TrayVisibility.OpenTaskbarSettings : null;

    /// <summary>The tray for this desktop: Avalonia's on Windows, our StatusNotifierItem on Linux, none elsewhere.</summary>
    public static async Task<ITrayPresenter?> CreateTrayAsync(DesktopEnvironment desktop, Application app, Func<bool> highContrast, AppPaths paths, ILog log)
    {
        if (desktop.IsWindows)
        {
            return new WindowsTrayPresenter(app, highContrast);
        }

        if (desktop.IsLinux)
        {
            return await SniTrayPresenter.TryStartAsync(paths.TrayIconsDirectory, highContrast, plasma: desktop.Tray == TrayDesktop.Kde, log);
        }

        return null;
    }

    /// <summary>Starts the tray icon on the UI thread; null when this desktop has none.</summary>
    public async Task<ITrayPresenter?> StartTrayAsync(Application app, Func<bool> highContrast) => Tray = await createTray(app, highContrast);

    public SystemRow OverlayRow(bool enabled) => OverlayAvailability.Row(desktop.Overlay, enabled);

    public SystemRow TrayRow()
    {
        var available = Tray?.IsAvailable == true;
        return TrayVisibility.Row(desktop.Tray, available, available ? Hidden() : null, OpenTaskbarSettings);
    }

    /// <summary>What the wizard's last step says about the tray icon.</summary>
    public TrayHint SetupTray() => new(desktop.Tray, () => Tray?.IsAvailable == true ? Hidden() : null, OpenTaskbarSettings);

    public async ValueTask DisposeAsync()
    {
        if (Tray is not null)
        {
            await Tray.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool? Hidden() => TrayVisibility.Hidden(desktop.Tray, Environment.ProcessPath, log);
}
