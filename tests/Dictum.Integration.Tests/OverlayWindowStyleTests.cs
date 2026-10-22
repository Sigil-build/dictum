using System.Runtime.Versioning;
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
    public async Task TheOverlayStaysClickThroughAndInactiveAcrossShows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var required = OverlayWindowStyle.Required;
        var styles = await RealAvalonia.RunAsync(ShowHideShow);

        Assert.All(styles, style => Assert.Equal(required, style & required));
    }

    [SupportedOSPlatform("windows")]
    private static long[] ShowHideShow()
    {
        using var desktop = OverlayDesktop.ForThisDesktop(DesktopEnvironment.Detect(), NullLog.Instance);
        var window = new OverlayWindow(desktop.Prepare);
        var handle = window.TryGetPlatformHandle()!.Handle;
        window.Show();
        var first = OverlayWindowStyle.ExtendedStyle(handle);
        window.Hide();
        window.Show();
        var second = OverlayWindowStyle.ExtendedStyle(handle);
        window.Close();
        return [first, second];
    }
}
