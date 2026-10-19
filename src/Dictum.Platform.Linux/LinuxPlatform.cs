using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;
using Dictum.Platform.Input;
using Dictum.Platform.Linux.DBus;
using SharpHook.Data;
using SharpHook.Providers;

namespace Dictum.Platform.Linux;

public static class LinuxPlatform
{
    public static PlatformServices Create(ILog log, Func<PlatformSettings> settings, string stateDirectory)
    {
        var session = SessionInfo.Detect();
        var portal = new Portal(log);
        var notifier = new DBusNotifier(portal, log);
        var autostart = new LinuxAutostart();

        return session.Type switch
        {
            SessionType.X11 => CreateX11(log, settings, session, portal, notifier, autostart),
            SessionType.Wayland => CreateWayland(log, settings, session, portal, notifier, autostart, stateDirectory),
            _ => UnsupportedPlatform.Create("Linux", "No graphical session found (neither WAYLAND_DISPLAY nor DISPLAY is set)."),
        };
    }

    private static PlatformServices CreateX11(
        ILog log, Func<PlatformSettings> settings, SessionInfo session, Portal portal, INotifier notifier, IAutostart autostart)
    {
        // XRecord listens and XTest posts without extra permissions; it must be chosen before any other SharpHook call.
        UioHookProvider.Instance.SetLinuxMode(LinuxMode.XRecord);
        var hotkeys = new SharpHookHotkeyService(log, "X11 XRecord hook (SharpHook)");
        IClipboard clipboard = XClipboard.IsAvailable ? new XClipboard() : new MissingClipboard("xclip or xsel");
        var injector = new ClipboardPasteInjector(
            clipboard,
            new SharpHookKeystrokeSender("XTest"),
            new XpropActiveApp(),
            hotkeys,
            () => settings().TerminalApps,
            log);

        return new PlatformServices(
            Name(session),
            () => hotkeys,
            injector,
            new SecretServiceStore(portal, log),
            autostart,
            notifier,
            ct => Task.FromResult<IReadOnlyList<PlatformCheck>>(
            [
                new(PlatformCheckNames.Session, HealthStatus.Ok(session.ToString())),
                new(PlatformCheckNames.Hotkeys, HealthStatus.Ok(hotkeys.Capabilities.Backend)),
                Tool(
                    PlatformCheckNames.Clipboard,
                    XClipboard.IsAvailable,
                    XClipboard.IsAvailable ? new XClipboard().Backend : XClipboard.Xclip,
                    "Install xclip: 'sudo pacman -S xclip' or 'sudo apt install xclip'."),
                Tool(PlatformCheckNames.ActiveWindow, XpropActiveApp.IsAvailable, XpropActiveApp.Program, "Install xprop (package xorg-xprop / x11-utils) so terminals get Ctrl+Shift+V."),
            ]));
    }

    private static PlatformServices CreateWayland(
        ILog log, Func<PlatformSettings> settings, SessionInfo session, Portal portal, INotifier notifier, IAutostart autostart, string stateDirectory)
    {
        var hotkeys = new PortalGlobalShortcuts(portal, log);
        IClipboard clipboard = WlClipboard.IsAvailable ? new WlClipboard() : new MissingClipboard(WlClipboard.Package);
        var keystrokes = new WaylandKeystrokes(
            session,
            () => settings().Injector,
            new Dictionary<InjectorBackend, IKeystrokeSender>
            {
                [InjectorBackend.Portal] = new PortalKeystrokes(portal, Path.Combine(stateDirectory, "remote-desktop.token"), log),
                [InjectorBackend.Ydotool] = new YdotoolKeystrokes(),
                [InjectorBackend.Wtype] = new WtypeKeystrokes(),
            },
            log);

        // On Wayland the focused app is unknown and modifier state is not observable; profiles set pasteShortcut.
        var injector = new ClipboardPasteInjector(clipboard, keystrokes, null, null, () => settings().TerminalApps, log);

        return new PlatformServices(
            Name(session),
            () => hotkeys,
            injector,
            new SecretServiceStore(portal, log),
            autostart,
            notifier,
            async ct => [
                new(PlatformCheckNames.Session, HealthStatus.Ok(session.ToString())),
                await PortalCheckAsync(portal, PlatformCheckNames.Hotkeys, PortalGlobalShortcuts.Interface, PortalGlobalShortcuts.CompositorHint, ct).ConfigureAwait(false),
                Tool(PlatformCheckNames.Clipboard, WlClipboard.IsAvailable, WlClipboard.Package, "Install wl-clipboard: 'sudo pacman -S wl-clipboard' or 'sudo apt install wl-clipboard'."),
                await PortalCheckAsync(portal, PlatformCheckNames.PastePortal, PortalKeystrokes.Interface, "Use ydotool instead: \"injector\": \"ydotool\".", ct).ConfigureAwait(false),
                new(PlatformCheckNames.PasteYdotool, YdotoolKeystrokes.IsAvailable
                    ? HealthStatus.Ok($"ydotoold at {YdotoolKeystrokes.SocketPath}")
                    : HealthStatus.Warning("ydotoold not running (optional fallback)", "'sudo pacman -S ydotool', then 'systemctl --user enable --now ydotool'.")),
                new(PlatformCheckNames.PasteOrder, HealthStatus.Ok(keystrokes.Backend)),
            ],
            PrepareAsync: async ct =>
            {
                if (Environment.ProcessPath is { } executable)
                {
                    await DesktopEntry.WriteAsync(InstallLocation.Daemon(executable, InstallLocation.AppImage), ct).ConfigureAwait(false);
                }

                await keystrokes.PrepareAsync(ct).ConfigureAwait(false);
            });
    }

    private static string Name(SessionInfo session) => $"Linux ({session})";

    private static async Task<PlatformCheck> PortalCheckAsync(Portal portal, string name, string iface, string hint, CancellationToken ct)
    {
        try
        {
            return await portal.GetVersionAsync(iface, ct).ConfigureAwait(false) is { } version
                ? new(name, HealthStatus.Ok($"{iface} v{version}"))
                : new(name, HealthStatus.Warning($"{iface} is not available", hint));
        }
        catch (Exception ex) when (ex is Tmds.DBus.Protocol.DBusExceptionBase or InvalidOperationException)
        {
            return new(name, HealthStatus.Error($"No D-Bus session bus: {ex.Message}", hint));
        }
    }

    private static PlatformCheck Tool(string name, bool present, string tool, string hint) =>
        new(name, present ? HealthStatus.Ok(tool) : HealthStatus.Error($"{tool} not found", hint));

    /// <summary>Clipboard stand-in that explains which tool to install.</summary>
    private sealed class MissingClipboard(string tool) : IClipboard
    {
        public string Backend => $"{tool} (missing)";

        public Task<string?> GetTextAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SetTextAsync(string text, CancellationToken ct) =>
            throw new InjectionUnavailableException($"{tool} is not installed.", "Run 'dictum doctor' for the install command.");
    }
}
