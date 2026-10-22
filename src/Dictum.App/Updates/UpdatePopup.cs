using System.Runtime.Versioning;
using Avalonia.Threading;
using Dictum.App.Resources;
using Dictum.App.Shell;
using Dictum.App.Tray;
using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Platform.Linux.DBus;

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

    public static IUpdatePopup For(DesktopEnvironment desktop, ILog log)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        if (desktop.IsWindows)
        {
            return new TrayBalloon(log);
        }

        if (desktop.IsLinux && ShowsOnLinux(InstallLocation.AppImage, Environment.ProcessPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            return new DesktopNotification(log);
        }

        return None;
    }

    /// <summary>
    /// Linux: an AppImage, or a program in the user's home folder, such as the tarball's <c>~/.local/lib/dictum</c>,
    /// which only the user updates. A program elsewhere, such as the AUR package's <c>/opt/dictum</c>, is updated by a
    /// package manager, so the notification would only repeat what it says.
    /// </summary>
    public static bool ShowsOnLinux(string? appImage, string? processPath, string? home)
    {
        if (appImage is not null)
        {
            return true;
        }

        if (processPath is null || string.IsNullOrEmpty(home))
        {
            return false;
        }

        return processPath.StartsWith(home.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

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
            _ = _notifications.ShowAsync(title, text, Strings.SettingsAboutOpenRelease, () => Dispatcher.UIThread.Post(clicked), CancellationToken.None);

        public void Dispose() => _notifications.Dispose();
    }
}
