using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Platform.Windows;

namespace Dictum.Host.Tests;

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

    // Windows lets a program send keys only to programs at its own integrity level or below, so an elevated Dictum
    // pastes everywhere, and only it reaches apps that run as administrator.
    [Fact]
    public void TheElevationRowSaysWhatAdministratorRightsAreFor()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Assert.Equal(
            new PlatformCheck(PlatformCheckNames.Elevation, HealthStatus.Warning(
                "Running as administrator",
                "Dictum needs administrator rights only to paste into apps that run as administrator; if you do not dictate into such apps, start it as a normal user.")),
            WindowsPlatform.ElevationCheck(administrator: true));
        Assert.Null(WindowsPlatform.ElevationCheck(administrator: false));
    }
}
