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
    public static PlatformServices Create(ILog log, Func<PlatformSettings> settings)
    {
        var session = SessionInfo.Detect();
        var portal = new Portal(log);
        var notifier = new DBusNotifier(portal, log);
        var autostart = new SystemdAutostart();

        return session.Type switch
        {
            SessionType.X11 => CreateX11(log, settings, session, notifier, autostart),
            SessionType.Wayland => CreateWayland(log, settings, session, portal, notifier, autostart),
            _ => UnsupportedPlatform.Create("Linux", "No graphical session found (neither WAYLAND_DISPLAY nor DISPLAY is set)."),
        };
    }

    private static PlatformServices CreateX11(
        ILog log, Func<PlatformSettings> settings, SessionInfo session, INotifier notifier, IAutostart autostart)
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
            $"Linux ({session})",
            () => hotkeys,
            injector,
            new UnavailableSecretStore(),
            autostart,
            notifier,
            ct => Task.FromResult<IReadOnlyList<PlatformCheck>>(
            [
                new("session", HealthStatus.Ok(session.ToString())),
                new("hotkeys", HealthStatus.Ok(hotkeys.Capabilities.Backend)),
                Tool("clipboard", XClipboard.IsAvailable, "xclip", "Install xclip: 'sudo pacman -S xclip' or 'sudo apt install xclip'."),
                Tool("active window", Tools.Exists("xprop"), "xprop", "Install xprop (package xorg-xprop / x11-utils) so terminals get Ctrl+Shift+V."),
            ]));
    }

    private static PlatformServices CreateWayland(
        ILog log, Func<PlatformSettings> settings, SessionInfo session, Portal portal, INotifier notifier, IAutostart autostart)
    {
        IClipboard clipboard = WlClipboard.IsAvailable ? new WlClipboard() : new MissingClipboard("wl-clipboard");
        var injector = new ClipboardPasteInjector(clipboard, null, null, null, () => settings().TerminalApps, log);

        return new PlatformServices(
            $"Linux ({session})",
            null,
            injector,
            new UnavailableSecretStore(),
            autostart,
            notifier,
            ct => Task.FromResult<IReadOnlyList<PlatformCheck>>(
            [
                new("session", HealthStatus.Ok(session.ToString())),
                Tool("clipboard", WlClipboard.IsAvailable, "wl-clipboard", "Install wl-clipboard: 'sudo pacman -S wl-clipboard' or 'sudo apt install wl-clipboard'."),
            ]));
    }

    internal static PlatformCheck Tool(string name, bool present, string tool, string hint) =>
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
