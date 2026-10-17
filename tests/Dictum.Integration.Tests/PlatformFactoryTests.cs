using Dictum.Core.Diagnostics;
using Dictum.Host;
using Dictum.Platform.Windows;

namespace Dictum.Integration.Tests;

/// <summary>Only builds the store; nothing is read from or written to it.</summary>
public sealed class PlatformFactoryTests
{
    [Fact]
    public void TheGuiGetsCredentialManagerOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Assert.IsType<CredentialManagerSecretStore>(PlatformFactory.CreateSecretStore(NullLog.Instance));
    }
}
