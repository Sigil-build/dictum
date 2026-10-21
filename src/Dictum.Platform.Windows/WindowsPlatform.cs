using System.Runtime.Versioning;
using System.Security.Principal;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;
using Dictum.Platform.Input;

namespace Dictum.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsPlatform
{
    /// <param name="terminalApps">Apps that paste with Ctrl+Shift+V, read at paste time.</param>
    public static PlatformServices Create(ILog log, Func<IReadOnlyCollection<string>> terminalApps)
    {
        // One hook serves both the hotkeys and the "wait until modifiers are released" check.
        var hotkeys = new SharpHookHotkeyService(log, "low-level keyboard hook (SharpHook)");
        var activeApp = new ForegroundAppProvider();
        var injector = new ClipboardPasteInjector(
            new Win32Clipboard(),
            new SharpHookKeystrokeSender("SendInput"),
            activeApp,
            hotkeys,
            terminalApps,
            log);

        return new PlatformServices(
            "Windows",
            () => hotkeys,
            injector,
            new CredentialManagerSecretStore(),
            new RunKeyAutostart(),
            NullNotifier.Instance,
            _ => Task.FromResult<IReadOnlyList<PlatformCheck>>(Diagnose(hotkeys, injector)),
            ActiveApp: activeApp);
    }

    private static List<PlatformCheck> Diagnose(SharpHookHotkeyService hotkeys, ClipboardPasteInjector injector)
    {
        var checks = new List<PlatformCheck>
        {
            new(PlatformCheckNames.Hotkeys, HealthStatus.Ok(hotkeys.Capabilities.Backend)),
            new(PlatformCheckNames.Paste, HealthStatus.Ok(injector.Capabilities.Backend)),
            new(PlatformCheckNames.Secrets, HealthStatus.Ok("Windows Credential Manager")),
        };

        using var identity = WindowsIdentity.GetCurrent();
        if (ElevationCheck(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) is { } elevation)
        {
            checks.Add(elevation);
        }

        return checks;
    }

    /// <summary>
    /// The <c>elevation</c> row, shown only while Dictum runs as administrator. Windows (UIPI) lets SendInput reach only
    /// apps at the sender's integrity level or below, so an elevated Dictum pastes into normal and elevated apps alike,
    /// and a normal one cannot paste into an elevated app. Nothing else in Dictum needs the rights.
    /// </summary>
    public static PlatformCheck? ElevationCheck(bool administrator) => administrator
        ? new(PlatformCheckNames.Elevation, HealthStatus.Warning(
            "Running as administrator",
            "Dictum needs administrator rights only to paste into apps that run as administrator; if you do not dictate into such apps, start it as a normal user."))
        : null;
}
