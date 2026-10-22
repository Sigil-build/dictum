using System.Runtime.CompilerServices;
using Dictum.Platform.Windows;

namespace Dictum.Integration.Tests;

/// <summary>
/// NOTIFYICONDATAW is passed to Shell_NotifyIcon by its managed layout, so the inline arrays that stand for its text
/// buffers must put every field where Windows reads it. Holds on any 64-bit OS: it checks the layout, not the call.
/// </summary>
public sealed class NotifyIconDataLayoutTests
{
    [Fact]
    public void TheStructIsAsLargeAsWindowsDocumentsIt()
    {
        Assert.SkipUnless(nint.Size == 8, "The documented size is that of 64-bit Windows");

        Assert.Equal(976, Unsafe.SizeOf<Win32.NotifyIconData>());
    }

    [Fact]
    public void EveryFieldIsWhereWindowsReadsIt()
    {
        Assert.SkipUnless(nint.Size == 8, "The offsets are those of 64-bit Windows");
        var data = default(Win32.NotifyIconData);

        Assert.Equal(32, Offset(ref data, ref data.Icon));
        Assert.Equal(40, Offset(ref data, ref data.Tip));
        Assert.Equal(296, Offset(ref data, ref data.State));
        Assert.Equal(304, Offset(ref data, ref data.Info));
        Assert.Equal(816, Offset(ref data, ref data.TimeoutOrVersion));
        Assert.Equal(820, Offset(ref data, ref data.InfoTitle));
        Assert.Equal(948, Offset(ref data, ref data.InfoFlags));
        Assert.Equal(952, Offset(ref data, ref data.Item));
        Assert.Equal(968, Offset(ref data, ref data.BalloonIcon));
    }

    private static int Offset<T>(ref Win32.NotifyIconData data, ref T field) =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<Win32.NotifyIconData, byte>(ref data), ref Unsafe.As<T, byte>(ref field));
}
