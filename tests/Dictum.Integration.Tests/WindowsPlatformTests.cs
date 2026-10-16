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
}
