using Dictum.Platform.Windows;

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
}
