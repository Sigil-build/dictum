using System.Diagnostics;

namespace Dictum.App.Tests.Updates;

/// <summary>
/// Windows heads the toast of a tray balloon, and lists its sender in Settings → Notifications, by the program's file
/// description. The build takes it from AssemblyTitle, which is the assembly's name, dictum-gui, unless it is set.
/// </summary>
public sealed class NotificationSenderTests
{
    [Fact]
    public void TheDesktopAppIsDescribedAsDictum()
    {
        var assembly = typeof(App).Assembly.Location;
        Assert.Equal("Dictum", FileVersionInfo.GetVersionInfo(assembly).FileDescription);

        // The program Windows starts carries the assembly's version resource.
        var program = Path.ChangeExtension(assembly, ".exe");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Dictum", FileVersionInfo.GetVersionInfo(program).FileDescription);
        }
    }
}
