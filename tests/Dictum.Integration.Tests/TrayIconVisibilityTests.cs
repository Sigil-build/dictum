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

    // Final review M7: Windows writes paths under Program Files and the system folders with the known folder's GUID.
    [Fact]
    public void AProgramFilesInstallIsFoundThroughItsKnownFolderGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Entry("1003", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Dictum\dictum-gui.exe", 1);
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dictum", "dictum-gui.exe");

        Assert.True(new TrayIconVisibility(_key).IsPromoted(installed));
    }

    [Theory]
    [InlineData(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\App\app.exe", Environment.SpecialFolder.ProgramFilesX86)]
    [InlineData(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\app.exe", Environment.SpecialFolder.System)]
    [InlineData(@"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe", Environment.SpecialFolder.Windows)]
    public void KnownFolderGuidsExpand(string stored, Environment.SpecialFolder folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var tail = stored[(stored.IndexOf('}') + 2)..];

        Assert.Equal(Path.Combine(Environment.GetFolderPath(folder), tail), TrayIconVisibility.Expand(stored));
    }

    [Fact]
    public void PlainAndUnknownPathsStayAsTheyAre()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        Assert.Equal(Exe, TrayIconVisibility.Expand(Exe));
        Assert.Equal(@"{00000000-0000-0000-0000-000000000000}\x.exe", TrayIconVisibility.Expand(@"{00000000-0000-0000-0000-000000000000}\x.exe"));
    }
}
