using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dictum.Platform.Windows;

/// <summary>
/// A balloon on a notification-area icon that another part of this process added, such as a UI toolkit's tray icon;
/// Windows 10 and 11 show the balloon as a toast. <see cref="Find"/> finds the icon by its window, one of this thread's
/// top-level windows, and by its id. A click on the balloon comes to that window as the icon's callback message, which
/// this class sees by subclassing the window; every message still goes on to the window's own procedure. One per
/// process; create, use and dispose it on the window's thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotifyIconBalloon : IDisposable
{
    /// <summary>The click on the balloon, as the callback message's lParam.</summary>
    public const uint BalloonUserClick = 0x0405;

    /// <summary>Ids tried, from the highest down: a toolkit numbers its icons from 1, one per icon it creates.</summary>
    private const uint HighestId = 32;

    private const uint NimModify = 0x1;
    private const uint NifInfo = 0x10;
    private const uint NiifNone = 0x0;
    private const uint NiifRespectQuietTime = 0x80;
    private const int GwlpWndProc = -4;
    private const int ClassNameLength = 256;
    private const int LowWord = 0xFFFF;

    private static readonly Lock Gate = new();

    /// <summary>The window this process subclassed, and its own procedure; kept once set, so messages always reach it.</summary>
    private static nint s_window;

    private static nint s_previous;

    private static NotifyIconBalloon? s_current;

    /// <summary>Held for the process's life: Windows calls the window's procedure through this delegate's thunk.</summary>
    private static readonly Win32.WindowProcedure s_procedure = WindowProcedure;

    private static readonly nint s_procedureAddress = Marshal.GetFunctionPointerForDelegate(s_procedure);

    private bool _disposed;

    private NotifyIconBalloon(nint window, uint id, uint callbackMessage)
    {
        Window = window;
        Id = id;
        CallbackMessage = callbackMessage;
    }

    public nint Window { get; }

    public uint Id { get; }

    /// <summary>The message the icon was added with, which carries its mouse and balloon events.</summary>
    public uint CallbackMessage { get; }

    /// <summary>The user clicked the balloon, or the toast it became. Raised on the window's thread.</summary>
    public event Action? Clicked;

    /// <summary>The address of the procedure that subclasses the window.</summary>
    public static nint Procedure => s_procedureAddress;

    /// <summary>
    /// The icon on this thread's window whose class name starts with <paramref name="classPrefix"/>: the highest id
    /// that Windows knows on that window. Null when there is no such window or icon. The window is subclassed from here
    /// until <see cref="Dispose"/>.
    /// </summary>
    public static NotifyIconBalloon? Find(string classPrefix, uint callbackMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(classPrefix);
        foreach (var window in ThreadWindows(classPrefix))
        {
            for (var id = HighestId; id > 0; id--)
            {
                if (Modify(window, id, title: "", text: ""))
                {
                    var balloon = new NotifyIconBalloon(window, id, callbackMessage);
                    balloon.Watch();
                    return balloon;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Shows <paramref name="title"/> (63 characters at most) and <paramref name="text"/> (255), without a sound
    /// unless the system plays one, and not in a new user's first hour. False when Windows refused it: the icon is gone.
    /// </summary>
    public bool Show(string title, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Modify(Window, Id, title ?? "", text);
    }

    /// <summary>Takes a balloon of this icon off the screen; true when the icon is still there.</summary>
    public bool Hide()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Modify(Window, Id, "", "");
    }

    /// <summary>Gives the window its own procedure back, unless another subclass came after; then clicks are just dropped.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (Gate)
        {
            if (s_current == this)
            {
                s_current = null;
            }

            if (s_window == Window && Win32.GetWindowLongPtr(Window, GwlpWndProc) == Procedure)
            {
                Win32.SetWindowLongPtr(Window, GwlpWndProc, s_previous);
                s_window = 0;
                s_previous = 0;
            }
        }
    }

    private void Watch()
    {
        lock (Gate)
        {
            s_current = this;
            if (s_window == Window)
            {
                return;
            }

            if (s_window != 0)
            {
                throw new InvalidOperationException("Another window is already subclassed for a balloon.");
            }

            s_previous = Win32.SetWindowLongPtr(Window, GwlpWndProc, Procedure);
            s_window = Window;
        }
    }

    /// <summary>NIM_MODIFY with NIF_INFO: an empty text takes the balloon away, which also tells whether the icon exists.</summary>
    private static bool Modify(nint window, uint id, string title, string text)
    {
        var data = default(Win32.NotifyIconData);
        data.Size = (uint)Unsafe.SizeOf<Win32.NotifyIconData>();
        data.Window = window;
        data.Id = id;
        data.Flags = NifInfo;
        data.InfoFlags = NiifNone | NiifRespectQuietTime;
        Copy(text, data.Info);
        Copy(title, data.InfoTitle);
        return Win32.ShellNotifyIcon(NimModify, ref data);
    }

    /// <summary>Copies as much of <paramref name="text"/> as fits before the terminating null, never half a surrogate pair.</summary>
    private static void Copy(string text, Span<char> target)
    {
        var capacity = target.Length;
        var length = Math.Min(text.Length, capacity - 1);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        text.AsSpan(0, length).CopyTo(target);
        target[length] = '\0';
    }

    /// <summary>This thread's top-level windows whose class name starts with <paramref name="prefix"/>.</summary>
    private static List<nint> ThreadWindows(string prefix)
    {
        var found = new List<nint>();
        Win32.EnumThreadWindows(Win32.GetCurrentThreadId(), (window, _) => CollectWindow(window, prefix, found), 0);
        return found;
    }

    private static int CollectWindow(nint window, string prefix, List<nint> found)
    {
        try
        {
            Span<char> name = stackalloc char[ClassNameLength];
            var length = Win32.GetClassName(window, ref MemoryMarshal.GetReference(name), ClassNameLength);
            if (length > 0 && name[..length].StartsWith(prefix, StringComparison.Ordinal))
            {
                found.Add(window);
            }
        }
        catch (Exception)
        {
            // An exception must not cross into Windows; the window is skipped.
        }

        return 1;
    }

    /// <summary>Sees the balloon's click, then hands every message to the window's own procedure.</summary>
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        nint previous;
        NotifyIconBalloon? current;
        lock (Gate)
        {
            previous = s_window == window ? s_previous : 0;
            current = s_current;
        }

        if (current is not null && current.Window == window && message == current.CallbackMessage && (uint)wParam == current.Id
            && (lParam & LowWord) == BalloonUserClick)
        {
            try
            {
                current.Clicked?.Invoke();
            }
            catch (Exception)
            {
                // An exception must not cross into Windows; the window still gets the message.
            }
        }

        return previous != 0 ? Win32.CallWindowProc(previous, window, message, wParam, lParam) : Win32.DefWindowProc(window, message, wParam, lParam);
    }
}
