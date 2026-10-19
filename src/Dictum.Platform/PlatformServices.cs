using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Hotkeys;
using Dictum.Platform.Injection;

namespace Dictum.Platform;

public enum NotificationKind
{
    Info,
    Error,
}

/// <summary>Desktop notifications; <see cref="NullNotifier"/> where there is no cheap way to show them.</summary>
public interface INotifier
{
    Task NotifyAsync(string summary, string body, NotificationKind kind, CancellationToken ct);
}

public sealed class NullNotifier : INotifier
{
    public static NullNotifier Instance { get; } = new();

    public Task NotifyAsync(string summary, string body, NotificationKind kind, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>What starts at login.</summary>
public enum LoginApp
{
    /// <summary><c>dictum daemon</c> alone, for machines without the desktop app.</summary>
    Daemon,

    /// <summary><c>dictum-gui</c>, which starts the daemon itself.</summary>
    DesktopApp,
}

/// <param name="Executable">Full path of <c>dictum</c> for <see cref="LoginApp.Daemon"/>, of <c>dictum-gui</c> otherwise;
/// inside an AppImage, the AppImage file.</param>
/// <param name="AppImage">The item runs through an AppImage, whose AppRun starts the CLI when the first argument is
/// <see cref="InstallLocation.AppImageCli"/>.</param>
public sealed record LoginItem(LoginApp App, string Executable, bool AppImage = false)
{
    public const string DesktopAppName = "dictum-gui";

    /// <summary>The CLI command that runs the daemon.</summary>
    public const string DaemonArgument = "daemon";

    public static string DesktopAppFile => OperatingSystem.IsWindows() ? DesktopAppName + ".exe" : DesktopAppName;

    public IReadOnlyList<string> Arguments => App == LoginApp.Daemon
        ? AppImage ? [InstallLocation.AppImageCli, DaemonArgument] : [DaemonArgument]
        : [];

    /// <summary>The quoted executable, then the arguments: the Run key value, the unit's ExecStart, an entry's Exec.</summary>
    public string CommandLine => string.Join(' ', [$"\"{Executable}\"", .. Arguments]);

    /// <summary>
    /// The desktop app when it sits next to <paramref name="dictum"/>, the daemon alone otherwise. Inside an AppImage
    /// (<paramref name="appImage"/>) the desktop app is always bundled, and the AppImage file is what lasts.
    /// </summary>
    public static LoginItem NextTo(string dictum, string? appImage = null)
    {
        if (appImage is not null)
        {
            return InstallLocation.DesktopApp(dictum, appImage);
        }

        var app = Path.Combine(Path.GetDirectoryName(dictum) ?? ".", DesktopAppFile);
        return File.Exists(app) ? new LoginItem(LoginApp.DesktopApp, app) : new LoginItem(LoginApp.Daemon, dictum);
    }
}

/// <summary>Starts Dictum when the user logs in. Only one kind of entry exists at a time.</summary>
public interface IAutostart
{
    /// <summary>Where the entry for <paramref name="app"/> lives, e.g. the unit file or the registry value.</summary>
    string Location(LoginApp app);

    /// <summary>What starts at login now, or null.</summary>
    LoginApp? Installed { get; }

    /// <summary>Writes the entry for <paramref name="item"/> and removes an entry of the other kind.</summary>
    Task InstallAsync(LoginItem item, CancellationToken ct);

    /// <summary>Removes every Dictum login entry.</summary>
    Task UninstallAsync(CancellationToken ct);
}

/// <summary>One line of <c>dictum doctor</c> output contributed by the platform.</summary>
public sealed record PlatformCheck(string Name, HealthStatus Status);

/// <summary>The implementations for the current OS and session.</summary>
/// <param name="CreateHotkeys">Hotkey backend; null when the platform has none.</param>
/// <param name="Unsupported">Why hotkeys and injection are unavailable on this platform, if they are.</param>
/// <param name="PrepareAsync">Slow setup to run when the daemon starts, e.g. a permission dialog.</param>
public sealed record PlatformServices(
    string Name,
    Func<IHotkeyService>? CreateHotkeys,
    ITextInjector Injector,
    ISecretStore Secrets,
    IAutostart? Autostart,
    INotifier Notifier,
    Func<CancellationToken, Task<IReadOnlyList<PlatformCheck>>> DiagnoseAsync,
    string? Unsupported = null,
    Func<CancellationToken, Task>? PrepareAsync = null);

/// <summary>Config values the platform reads at use time (they can change on hot reload).</summary>
/// <param name="TerminalApps">Apps that paste with Ctrl+Shift+V.</param>
/// <param name="Injector">Preferred paste backend on Linux Wayland.</param>
public sealed record PlatformSettings(IReadOnlyCollection<string> TerminalApps, InjectorBackend Injector);
