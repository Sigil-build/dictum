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

/// <summary>Starts the daemon when the user logs in.</summary>
public interface IAutostart
{
    /// <summary>Where the entry lives, e.g. the unit file or registry key.</summary>
    string Location { get; }

    bool IsInstalled { get; }

    Task InstallAsync(string executable, CancellationToken ct);

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
