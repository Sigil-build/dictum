using Avalonia;
using Avalonia.Platform;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux;
using Dictum.Platform.Linux.DBus;
using Dictum.Platform.Linux.X11;
using Dictum.Platform.Windows;

namespace Dictum.App.Overlay;

/// <summary>What a dictation start learns from the desktop: the pointer's screen and the reduced-motion setting.</summary>
public sealed record OverlayProbe(ScreenHint Hint, bool ReducedMotion)
{
    public static readonly OverlayProbe Unknown = new(ScreenHint.None, false);
}

/// <summary>The overlay's OS side; a fake in tests.</summary>
public interface IOverlayDesktop
{
    /// <summary>Makes the window click-through and never activated, before it is first shown.</summary>
    void Prepare(IPlatformHandle handle);

    /// <summary>Never throws, except when <paramref name="ct"/> was cancelled before it started.</summary>
    Task<OverlayProbe> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// The desktop the app runs on (spec 5.4, 5.5, spike findings):
/// - Windows: Win32 styles and the cursor.
/// - X11 and XWayland: override-redirect.
/// - The pointer's screen: XQueryPointer on an X11 session, KWin on KDE Wayland.
/// - Reduced motion: each desktop's own setting.
/// </summary>
public sealed class OverlayDesktop : IOverlayDesktop, IDisposable
{
    private readonly ILog _log;
    private readonly SessionInfo? _session;
    private readonly KWinOutputs? _kwin;

    private OverlayDesktop(ILog log, SessionInfo? session)
    {
        _log = log;
        _session = session;
        _kwin = session is { Type: SessionType.Wayland, Desktop: Desktop.Kde } ? new KWinOutputs() : null;
    }

    /// <summary>Knows nothing and changes nothing: tests, and desktops without an overlay path.</summary>
    public static IOverlayDesktop None { get; } = new Blind();

    public static OverlayDesktop ForThisDesktop(ILog log) => new(log, OperatingSystem.IsLinux() ? SessionInfo.Detect() : null);

    public void Prepare(IPlatformHandle handle)
    {
        try
        {
            if (OperatingSystem.IsWindows() && handle.HandleDescriptor == "HWND")
            {
                OverlayWindowStyle.Apply(handle.Handle);
            }
            else if (OperatingSystem.IsLinux() && handle.HandleDescriptor == "XID" && !X11Overlay.Prepare((nuint)handle.Handle))
            {
                _log.Warn("No X display; the overlay window is not click-through");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.Warn($"The overlay window could not be made click-through: {ex.Message}");
        }
    }

    public Task<OverlayProbe> ProbeAsync(CancellationToken ct) => Task.Run(() => ProbeCoreAsync(ct), ct);

    public void Dispose() => _kwin?.Dispose();

    private async Task<OverlayProbe> ProbeCoreAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var cursor = OverlayWindowStyle.CursorPosition();
                var hint = cursor is { } at ? new ScreenHint(new PixelPoint(at.X, at.Y), null) : ScreenHint.None;
                return new OverlayProbe(hint, !OverlayWindowStyle.AnimationsEnabled());
            }

            if (_session is { } session)
            {
                var hint = await LinuxHintAsync(session, ct).ConfigureAwait(false);
                var reduced = await DesktopMotion.ReducedAsync(session, Environment.GetEnvironmentVariable, ct).ConfigureAwait(false);
                return new OverlayProbe(hint, reduced);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.Debug($"Overlay probe: {ex.Message}");
        }

        return OverlayProbe.Unknown;
    }

    private async Task<ScreenHint> LinuxHintAsync(SessionInfo session, CancellationToken ct)
    {
        if (_kwin is not null)
        {
            return await _kwin.ActiveOutputAsync(ct).ConfigureAwait(false) is { } output ? new ScreenHint(null, output) : ScreenHint.None;
        }

        // Under XWayland the X pointer freezes over Wayland windows (spike), so only a real X11 session asks X.
        return session.Type == SessionType.X11 && X11Overlay.Pointer() is { } pointer
            ? new ScreenHint(new PixelPoint(pointer.X, pointer.Y), null)
            : ScreenHint.None;
    }

    private sealed class Blind : IOverlayDesktop
    {
        public void Prepare(IPlatformHandle handle)
        {
        }

        public Task<OverlayProbe> ProbeAsync(CancellationToken ct) => Task.FromResult(OverlayProbe.Unknown);
    }
}
