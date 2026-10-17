using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dictum.Platform;
using Dictum.Platform.Windows;
using Microsoft.Win32;

namespace Dictum.Integration.Tests;

/// <summary>Real Win32 clipboard and Credential Manager; skipped on other OSes.</summary>
public class WindowsPlatformTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClipboardRoundTripsUnicodeText()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var clipboard = new Win32Clipboard();
        var original = await clipboard.GetTextAsync(_ct);
        try
        {
            await clipboard.SetTextAsync("Привіт, Dictum ✓", _ct);

            Assert.Equal("Привіт, Dictum ✓", await clipboard.GetTextAsync(_ct));
        }
        finally
        {
            if (original is not null)
            {
                await clipboard.SetTextAsync(original, _ct);
            }
        }
    }

    [Fact]
    public async Task AClipboardHeldForHalfASecondIsWaitedFor()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        // The clipboard history, a clipboard manager or an app that renders its data late can hold it this long.
        await using var hold = await ClipboardHold.StartAsync(TimeSpan.FromMilliseconds(500));
        var waited = Stopwatch.StartNew();

        await new Win32Clipboard().GetTextAsync(_ct);

        Assert.True(waited.Elapsed >= TimeSpan.FromMilliseconds(400), $"The read did not wait for the holder: {waited.Elapsed}");
    }

    // The platform analyzer does not carry the IsWindows check into the lambdas of the next two tests.
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AClipboardHeldPastTheTimeoutIsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        await using var hold = await ClipboardHold.StartAsync(TimeSpan.FromSeconds(1));
        var clipboard = new Win32Clipboard(openTimeout: TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAsync<Win32Exception>(() => clipboard.GetTextAsync(_ct));

        Assert.Contains("in use by another application", error.Message);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CancellingStopsWaitingForABusyClipboard()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        await using var hold = await ClipboardHold.StartAsync(TimeSpan.FromSeconds(1));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Win32Clipboard().GetTextAsync(cts.Token));
    }

    [Fact]
    public async Task CredentialManagerStoresReadsAndDeletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var store = new CredentialManagerSecretStore();
        var key = $"test-{Guid.NewGuid():N}";
        try
        {
            await store.SetAsync(key, "sk-тест-123", _ct);
            Assert.Equal("sk-тест-123", await store.GetAsync(key, _ct));
        }
        finally
        {
            await store.DeleteAsync(key, _ct);
        }

        Assert.Null(await store.GetAsync(key, _ct));
    }

    [Fact]
    public async Task TheRunKeyHoldsOneEntryOfEitherKind()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        // A key of its own: the real Run key belongs to the user's installed Dictum.
        var keyPath = $@"Software\Dictum-Tests\Run-{Guid.NewGuid():N}";
        var autostart = new RunKeyAutostart(keyPath);
        try
        {
            await autostart.InstallAsync(new LoginItem(LoginApp.Daemon, @"C:\Tools\Dictum\dictum.exe"), _ct);
            var daemon = autostart.Installed;
            await autostart.InstallAsync(new LoginItem(LoginApp.DesktopApp, @"C:\Tools\Dictum\dictum-gui.exe"), _ct);

            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"C:\\Tools\\Dictum\\dictum-gui.exe\"", key!.GetValue("Dictum"));
            }

            Assert.Equal((LoginApp.Daemon, LoginApp.DesktopApp), (daemon, autostart.Installed));
            await autostart.UninstallAsync(_ct);
            Assert.Null(autostart.Installed);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Dictum-Tests", throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [SupportedOSPlatform("windows")]
    [InlineData("\"C:\\Tools\\Dictum\\dictum.exe\" daemon", LoginApp.Daemon)]
    [InlineData("\"C:\\Tools\\Dictum\\dictum-gui.exe\"", LoginApp.DesktopApp)]
    public void RunKeyValuesAreReadBack(string command, LoginApp app) => Assert.Equal(app, RunKeyAutostart.Parse(command));

    [Fact]
    public void TheOverlayStyleMakesAWindowClickThroughAndInactive()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var window = OverlayTestWindow.Create();
        try
        {
            Assert.Equal(0, OverlayWindowStyle.ExtendedStyle(window) & OverlayWindowStyle.Required);

            OverlayWindowStyle.Apply(window);

            Assert.Equal(OverlayWindowStyle.Required, OverlayWindowStyle.ExtendedStyle(window) & OverlayWindowStyle.Required);
            _ = OverlayWindowStyle.AnimationsEnabled();
        }
        finally
        {
            OverlayTestWindow.Destroy(window);
        }
    }

    [Fact]
    public void TheTrayIconSizeFollowsTheDisplayScale()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Assert.InRange(TaskbarInfo.SmallIconSize(), 16, 64);
    }

    [Theory]
    [SupportedOSPlatform("windows")]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    [InlineData("1", false)]
    public void TheTaskbarThemeIsReadFromItsRegistryValue(object? value, bool light) => Assert.Equal(light, TaskbarInfo.IsLight(value));
}

/// <summary>Keeps the clipboard open from another thread, the way a slow clipboard manager does.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ClipboardHold(Task closed) : IAsyncDisposable
{
    /// <summary>Returns once the clipboard is open.</summary>
    public static async Task<ClipboardHold> StartAsync(TimeSpan duration)
    {
        // Asynchronous continuations: the test must not carry on on the thread that is about to close the clipboard.
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The clipboard is closed on the thread that opened it, so the hold gets a thread of its own. It opens with a
        // window of its own too: a second open with no window succeeds while the first one, also with no window, holds it.
        var thread = new Thread(() =>
        {
            var owner = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
            try
            {
                var waited = Stopwatch.StartNew();
                while (!OpenClipboard(owner))
                {
                    if (waited.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        opened.SetException(new InvalidOperationException("Could not open the clipboard to hold it."));
                        return;
                    }

                    Thread.Sleep(5);
                }

                opened.SetResult();
                Thread.Sleep(duration);
                CloseClipboard();
                released.SetResult();
            }
            finally
            {
                DestroyWindow(owner);
            }
        })
        { IsBackground = true };
        thread.Start();

        await opened.Task;
        return new ClipboardHold(released.Task);
    }

    /// <summary>Waits for the hold to end, so a failed test does not leave the clipboard held for the next one.</summary>
    public async ValueTask DisposeAsync() => await closed;

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    private const nint HwndMessage = -3;

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}

/// <summary>A plain hidden popup window to put the overlay's styles on.</summary>
[SupportedOSPlatform("windows")]
internal static partial class OverlayTestWindow
{
    private const uint WsPopup = 0x80000000;

    public static nint Create()
    {
        var window = CreateWindowEx(0, "STATIC", null, WsPopup, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual((nint)0, window);
        return window;
    }

    public static void Destroy(nint window) => DestroyWindow(window);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
