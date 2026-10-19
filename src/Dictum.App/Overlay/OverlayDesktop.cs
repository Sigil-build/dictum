using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Dictum.App.Shell;
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
    /// <summary>Makes the window click-through and never activated; called before it is first shown.</summary>
    void Prepare(Window window);

    /// <summary>Never throws, except when <paramref name="ct"/> was cancelled before it started.</summary>
    Task<OverlayProbe> ProbeAsync(CancellationToken ct);
}

/// <summary>
/// The desktop the app runs on:
/// - Windows: Win32 styles and the cursor.
/// - X11 and XWayland: override-redirect.
/// - The pointer's screen: XQueryPointer on an X11 session, KWin on KDE Wayland.
/// - Reduced motion: each desktop's own setting.
/// </summary>
public sealed class OverlayDesktop : IOverlayDesktop, IDisposable
{
    private readonly ILog _log;
    private readonly DesktopEnvironment _desktop;
    private readonly KWinOutputs? _kwin;

    private OverlayDesktop(ILog log, DesktopEnvironment desktop)
    {
        _log = log;
        _desktop = desktop;
        _kwin = desktop.Session is { Type: SessionType.Wayland, Desktop: Desktop.Kde } ? new KWinOutputs() : null;
    }

    /// <summary>Knows nothing and changes nothing: tests, and desktops without an overlay path.</summary>
    public static IOverlayDesktop None { get; } = new Blind();

    public static OverlayDesktop ForThisDesktop(DesktopEnvironment desktop, ILog log) => new(log, desktop);

    public void Prepare(Window window)
    {
        try
        {
            if (_desktop.IsWindows)
            {
                PrepareWin32(window);
            }
            else if (_desktop.IsLinux && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle
                && !X11Overlay.Prepare((nuint)handle.Handle))
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

    /// <summary>
    /// Avalonia rebuilds the extended style on every Show, so the bits go through its style callback;
    /// the window becomes layered during the first Show, and its alpha is set once it has opened.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void PrepareWin32(Window window)
    {
        Win32Properties.AddWindowStylesCallback(window, static (style, exStyle) => (style, exStyle | (uint)OverlayWindowStyle.Required));
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
            {
                OverlayWindowStyle.MakeOpaque(handle.Handle);
            }
        };
    }

    public void Dispose() => _kwin?.Dispose();

    private async Task<OverlayProbe> ProbeCoreAsync(CancellationToken ct)
    {
        try
        {
            if (_desktop.IsWindows)
            {
                var cursor = OverlayWindowStyle.CursorPosition();
                var hint = cursor is { } at ? new ScreenHint(new PixelPoint(at.X, at.Y), null) : ScreenHint.None;
                return new OverlayProbe(hint, !OverlayWindowStyle.AnimationsEnabled());
            }

            if (_desktop.Session is { } session)
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

        // Under XWayland the X pointer freezes over Wayland windows, so only a real X11 session asks X.
        return session.Type == SessionType.X11 && X11Overlay.PointerPosition() is { } pointer
            ? new ScreenHint(new PixelPoint(pointer.X, pointer.Y), null)
            : ScreenHint.None;
    }

    private sealed class Blind : IOverlayDesktop
    {
        public void Prepare(Window window)
        {
        }

        public Task<OverlayProbe> ProbeAsync(CancellationToken ct) => Task.FromResult(OverlayProbe.Unknown);
    }
}
