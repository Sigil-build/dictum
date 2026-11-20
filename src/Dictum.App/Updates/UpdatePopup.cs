using System.Runtime.Versioning;
using Avalonia.Threading;
using Dictum.App.Resources;
using Dictum.App.Shell;
using Dictum.App.Tray;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux.DBus;
using Dictum.Updates;

namespace Dictum.App.Updates;

/// <summary>The OS's notification that a new version is out; a fake in tests.</summary>
public interface IUpdatePopup
{
    /// <summary>
    /// Shows <paramref name="title"/> and <paramref name="text"/>; <paramref name="clicked"/> runs on the UI thread
    /// when the user clicks the notification. UI thread; never throws.
    /// </summary>
    /// <param name="tray">The tray icon, which carries the notification on Windows; null while there is none.</param>
    void Show(ITrayPresenter? tray, string title, string text, Action clicked);
}

/// <summary>
/// The update notification for this desktop: a balloon on the tray icon on Windows; on Linux a desktop notification,
/// but only where the user updates Dictum by hand, as an AppImage or the tarball; none elsewhere.
/// </summary>
public static class UpdatePopup
{
    public static IUpdatePopup None { get; } = new NoPopup();

    public static IUpdatePopup For(DesktopEnvironment desktop, Installation installation, ILog log)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(installation);
        if (desktop.IsWindows)
        {
            return new TrayBalloon(log);
        }

        if (desktop.IsLinux && ShowsOnLinux(installation.Kind))
        {
            return new DesktopNotification(log);
        }

        return None;
    }

    /// <summary>
    /// Linux: an AppImage, the tarball and a portable copy, which Dictum or the user updates. A package manager's
    /// install, such as the AUR package's <c>/opt/dictum</c>, is updated by it, so the notification would only repeat it.
    /// </summary>
    public static bool ShowsOnLinux(InstallKind kind) => kind != InstallKind.PackageManager;

    private sealed class NoPopup : IUpdatePopup
    {
        public void Show(ITrayPresenter? tray, string title, string text, Action clicked)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class TrayBalloon(ILog log) : IUpdatePopup
    {
        public void Show(ITrayPresenter? tray, string title, string text, Action clicked)
        {
            try
            {
                if (tray is not WindowsTrayPresenter windows || !windows.Notify(title, text, clicked))
                {
                    log.Info("No update notification: the tray icon cannot be reached");
                }
            }
            catch (Exception ex)
            {
                // The tray and About Dictum still say so; a notification is not worth the app.
                log.Warn($"No update notification: {ex.Message}");
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private sealed class DesktopNotification(ILog log) : IUpdatePopup, IDisposable
    {
        private readonly DesktopNotifications _notifications = new(log);

        public void Show(ITrayPresenter? tray, string title, string text, Action clicked) =>
            _ = _notifications.ShowAsync(title, text, Strings.SettingsAbout, () => Dispatcher.UIThread.Post(clicked), CancellationToken.None);

        public void Dispose() => _notifications.Dispose();
    }
}
