using System.Runtime.Versioning;
using Avalonia;
using Dictum.App.Overlay;
using Dictum.App.Shell;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Windows;

namespace Dictum.Integration.Tests;

/// <summary>
/// The overlay's Win32 styles on a real Avalonia window, which rebuilds GWL_EXSTYLE on every Show.
/// Runs on a locked desktop too.
/// </summary>
public class OverlayWindowStyleTests
{
    [Fact]
    public void TheOverlayStaysClickThroughAndInactiveAcrossShows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var required = OverlayWindowStyle.Required;
        var styles = ShowHideShow();

        Assert.All(styles, style => Assert.Equal(required, style & required));
    }

    /// <summary>Avalonia's Win32 platform starts once per process, on a thread of its own.</summary>
    [SupportedOSPlatform("windows")]
    private static long[] ShowHideShow()
    {
        long[]? styles = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
                using var desktop = OverlayDesktop.ForThisDesktop(DesktopEnvironment.Detect(), NullLog.Instance);
                var window = new OverlayWindow(desktop.Prepare);
                var handle = window.TryGetPlatformHandle()!.Handle;
                window.Show();
                var first = OverlayWindowStyle.ExtendedStyle(handle);
                window.Hide();
                window.Show();
                var second = OverlayWindowStyle.ExtendedStyle(handle);
                window.Close();
                styles = [first, second];
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure is null ? styles! : throw new InvalidOperationException("The Avalonia thread failed.", failure);
    }
}
