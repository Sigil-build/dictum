using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux.DBus;

namespace Dictum.Integration.Tests.Linux;

/// <summary>
/// The Secret Service store on a real session bus. Linux only; CI runs it inside <c>dbus-run-session</c>, where no
/// Secret Service owns its name.
/// </summary>
public sealed class SecretServiceStoreTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithABusButNoSecretServiceOnItTheStoreSaysNoneIsRunning()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")), "No session bus; run inside dbus-run-session");
        using var portal = new Portal(NullLog.Instance);
        using var store = new SecretServiceStore(portal, NullLog.Instance);

        SecretStoreUnavailableException ex;
        try
        {
            await store.GetAsync("dictum-test-no-such-key", _ct);
            Assert.Skip("A Secret Service is running on this session bus");
            return;
        }
        catch (SecretStoreUnavailableException unavailable)
        {
            ex = unavailable;
        }

        Assert.Contains("No Secret Service is running", ex.Message, StringComparison.Ordinal);
    }
}
