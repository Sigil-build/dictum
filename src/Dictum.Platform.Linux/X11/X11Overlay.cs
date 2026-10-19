using System.Runtime.InteropServices;

namespace Dictum.Platform.Linux.X11;

/// <summary>
/// The listening overlay's X11 side, the same under XWayland: override-redirect, a notification window type and an
/// empty input region, all set before the window is first mapped; the pointer on an X11 session; and the X resource
/// database, for Xft.dpi. One display connection serves the process and stays open.
/// </summary>
public static unsafe partial class X11Overlay
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXext = "libXext.so.6";
    private const nuint OverrideRedirectMask = 1 << 9;
    private const int PropModeReplace = 0;
    private const nuint AtomType = 4;
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;

    private static readonly Lazy<nint> Display = new(() =>
        Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? XOpenDisplay(0) : 0);

    /// <summary>Makes <paramref name="window"/> a click-through notification; false without an X display.</summary>
    public static bool Prepare(nuint window)
    {
        var display = Display.Value;
        if (display == 0)
        {
            return false;
        }

        var attributes = new SetWindowAttributes { OverrideRedirect = 1 };
        XChangeWindowAttributes(display, window, OverrideRedirectMask, ref attributes);
        var type = XInternAtom(display, "_NET_WM_WINDOW_TYPE", 0);
        var notification = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", 0);
        XChangeProperty(display, window, type, AtomType, 32, PropModeReplace, &notification, 1);
        Rectangle none = default;
        XShapeCombineRectangles(display, window, ShapeInput, 0, 0, &none, 0, ShapeSet, 0);
        XFlush(display);
        return true;
    }

    /// <summary>The pointer in root-window pixels. Under XWayland it only moves over X11 windows, so KDE Wayland asks KWin.</summary>
    public static (int X, int Y)? Pointer()
    {
        var display = Display.Value;
        return display != 0 && XQueryPointer(display, XDefaultRootWindow(display), out _, out _, out var x, out var y, out _, out _, out _) != 0
            ? (x, y)
            : null;
    }

    /// <summary>What <c>xrdb -query</c> prints, or null without an X display.</summary>
    public static string? ResourceDatabase()
    {
        var display = Display.Value;
        return display == 0 ? null : Marshal.PtrToStringUTF8(XResourceManagerString(display));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetWindowAttributes
    {
        public nuint BackgroundPixmap;
        public nuint BackgroundPixel;
        public nuint BorderPixmap;
        public nuint BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public nuint BackingPlanes;
        public nuint BackingPixel;
        public int SaveUnder;
        public nint EventMask;
        public nint DoNotPropagateMask;
        public int OverrideRedirect;
        public nuint Colormap;
        public nuint Cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
    }

    [LibraryImport(LibX11)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(LibX11)]
    private static partial int XChangeWindowAttributes(nint display, nuint window, nuint mask, ref SetWindowAttributes attributes);

    [LibraryImport(LibX11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(LibX11)]
    private static partial int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, nuint* data, int count);

    [LibraryImport(LibX11)]
    private static partial int XFlush(nint display);

    [LibraryImport(LibX11)]
    private static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport(LibX11)]
    private static partial int XQueryPointer(
        nint display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int x, out int y, out uint mask);

    [LibraryImport(LibX11)]
    private static partial nint XResourceManagerString(nint display);

    [LibraryImport(LibXext)]
    private static partial int XShapeCombineRectangles(
        nint display, nuint window, int kind, int x, int y, Rectangle* rectangles, int count, int operation, int ordering);
}
