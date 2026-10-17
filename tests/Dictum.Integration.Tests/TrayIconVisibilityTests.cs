using System.Runtime.Versioning;
using Dictum.Platform.Windows;
using Microsoft.Win32;

namespace Dictum.Integration.Tests;

/// <summary>A scratch key under HKCU\Software\DictumTests, never the real NotifyIconSettings; skipped off Windows.</summary>
public sealed class TrayIconVisibilityTests : IDisposable
{
    private const string Exe = @"D:\Tools\dictum\dictum-gui.exe";
    private readonly string _key = $@"Software\DictumTests\NotifyIconSettings-{Guid.NewGuid():N}";

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private void Entry(string id, string executable, int? promoted)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_key}\{id}");
        key.SetValue("ExecutablePath", executable);
        if (promoted is { } value)
        {
            key.SetValue("IsPromoted", value, RegistryValueKind.DWord);
        }
    }

    [Fact]
    public void APromotedIconIsVisible()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Entry("1001", @"C:\Windows\explorer.exe", 0);
        Entry("1002", Exe, 1);

        Assert.True(new TrayIconVisibility(_key).IsPromoted(Exe));
    }

    [Fact]
    public void AnIconWithoutIsPromotedIsInTheOverflow()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Entry("1002", Exe, null);

        Assert.False(new TrayIconVisibility(_key).IsPromoted(Exe.ToUpperInvariant()));
    }

    [Fact]
    public void AnIconWindowsHasNotSeenIsUnknown()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Entry("1001", @"C:\Windows\explorer.exe", 1);

        Assert.Null(new TrayIconVisibility(_key).IsPromoted(Exe));
        Assert.Null(new TrayIconVisibility(_key + @"\missing").IsPromoted(Exe));
    }
}
