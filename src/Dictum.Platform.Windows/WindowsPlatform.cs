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
        var injector = new ClipboardPasteInjector(
            new Win32Clipboard(),
            new SharpHookKeystrokeSender("SendInput"),
            new ForegroundAppProvider(),
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
            _ => Task.FromResult<IReadOnlyList<PlatformCheck>>(Diagnose(hotkeys, injector)));
    }

    private static List<PlatformCheck> Diagnose(SharpHookHotkeyService hotkeys, ClipboardPasteInjector injector)
    {
        var checks = new List<PlatformCheck>
        {
            new("hotkeys", HealthStatus.Ok(hotkeys.Capabilities.Backend)),
            new("paste", HealthStatus.Ok(injector.Capabilities.Backend)),
            new("secrets", HealthStatus.Ok("Windows Credential Manager")),
        };

        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            checks.Add(new("elevation", HealthStatus.Warning(
                "Running as administrator",
                "Start Dictum as a normal user: pasting only works into apps at the same or lower privilege level.")));
        }

        return checks;
    }
}
