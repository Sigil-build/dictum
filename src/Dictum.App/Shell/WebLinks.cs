using System.ComponentModel;
using System.Diagnostics;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Shell;

/// <summary>Opens web pages in the user's browser.</summary>
public static class WebLinks
{
    /// <summary>
    /// Hands <paramref name="url"/> to the OS, which opens it in the default browser: the shell on Windows,
    /// <c>xdg-open</c> on Linux. Never throws.
    /// </summary>
    /// <param name="start">Starts the browser; <see cref="Process.Start(ProcessStartInfo)"/> when left out.</param>
    /// <returns>False when nothing could be started; the log then has a warning that names the URL.</returns>
    public static bool Open(Uri url, ILog log, Func<ProcessStartInfo, Process?>? start = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(log);
        // A handler that starts and then fails tells Dictum nothing, so the URL is in the log either way.
        log.Info($"Opening {url.AbsoluteUri} in the browser");
        try
        {
            (start ?? Process.Start)(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No xdg-open, or an AppImage's environment that breaks the handler: the link does nothing, and the log
            // keeps the URL so the user can open it by hand.
            log.Warn($"Could not open {url.AbsoluteUri} in a browser: {ex.Message}");
            return false;
        }
    }
}
