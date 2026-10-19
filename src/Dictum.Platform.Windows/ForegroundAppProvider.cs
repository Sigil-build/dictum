using System.Runtime.Versioning;
using Dictum.Platform.Injection;

namespace Dictum.Platform.Windows;

/// <summary>Process name of the foreground window, e.g. "windowsterminal".</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class ForegroundAppProvider : IActiveAppProvider
{
    private const int ImageNameCapacity = 1024;

    public string? GetActiveApp()
    {
        var window = Win32.GetForegroundWindow();
        if (window == 0 || Win32.GetWindowThreadProcessId(window, out var processId) == 0)
        {
            return null;
        }

        var process = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[ImageNameCapacity];
            uint size = ImageNameCapacity;
            return Win32.QueryFullProcessImageName(process, 0, buffer, ref size)
                ? Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size)).ToLowerInvariant()
                : null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}
