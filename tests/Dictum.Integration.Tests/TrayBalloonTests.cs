using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Dictum.App.Tray;
using Dictum.Platform.Windows;

namespace Dictum.Integration.Tests;

/// <summary>
/// The update notification's way to Dictum's tray icon on Avalonia's real Win32 platform: the balloon finds the icon that
/// <see cref="WindowsTrayPresenter"/> added, Windows takes a balloon request for it, a click on the balloon comes back,
/// and Avalonia's own icon events still arrive. No notification is shown: the request only takes a balloon away.
/// The icon shows in the tray, or its overflow, for a moment, and Windows then lists the test host in its taskbar
/// settings (Other system tray icons) for good, so it is enabled with DICTUM_INTEGRATION=1. Windows only.
/// </summary>
public sealed partial class TrayBalloonTests
{
    private const uint WmLButtonUp = 0x0202;

    [Fact]
    public async Task TheBalloonReachesDictumsTrayIconAndItsClickComesBack()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1",
            "Set DICTUM_INTEGRATION=1 to run: Windows keeps a tray icon it has seen in its taskbar settings");

        var (found, hidden, clicks, activations, restored) = await RealAvalonia.RunAsync(Probe);

        Assert.True(found, "no tray icon of Avalonia's on this thread's message window");
        Assert.True(hidden, "Windows refused a balloon request for the icon");
        Assert.Equal(1, clicks);
        Assert.Equal(1, activations);
        Assert.True(restored, "the window did not get its own procedure back");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(bool Found, bool Hidden, int Clicks, int Activations, bool Restored)> Probe()
    {
        var presenter = new WindowsTrayPresenter(Application.Current!, () => false);
        var activations = 0;
        presenter.Activated += () => activations++;
        presenter.Show(new TrayView(TrayGlyph.Idle, "Dictum · balloon test", false, [TrayMenuItem.Action("Quit Dictum", TrayCommand.Quit)], TrayCommand.Settings));
        await RealAvalonia.SettleAsync();
        try
        {
            using var balloon = NotifyIconBalloon.Find(WindowsTrayPresenter.MessageWindowClass, WindowsTrayPresenter.TrayCallbackMessage);
            if (balloon is null)
            {
                return (false, false, 0, 0, false);
            }

            var original = GetWindowLongPtr(balloon.Window, -4);
            var clicks = 0;
            balloon.Clicked += () => clicks++;
            var hidden = balloon.Hide();

            // What Windows sends the icon's window when the user clicks the balloon, then a click on the icon itself.
            SendMessage(balloon.Window, balloon.CallbackMessage, (nint)balloon.Id, (nint)NotifyIconBalloon.BalloonUserClick);
            SendMessage(balloon.Window, balloon.CallbackMessage, (nint)balloon.Id, (nint)WmLButtonUp);
            await RealAvalonia.SettleAsync();

            balloon.Dispose();
            var restored = GetWindowLongPtr(balloon.Window, -4) != NotifyIconBalloon.Procedure && original == NotifyIconBalloon.Procedure;
            return (true, hidden, clicks, activations, restored);
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint window, int index);
}
