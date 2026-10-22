using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DisableRuntimeMarshalling]

namespace Dictum.Platform.Windows;

internal static unsafe partial class Win32
{
    public const uint CfUnicodeText = 13;
    public const uint GmemMoveable = 0x0002;
    public const nint HwndMessage = -3;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint CredTypeGeneric = 1;
    public const uint CredPersistLocalMachine = 2;
    public const int ErrorNotFound = 1168;
    public const int GwlExStyle = -20;
    public const uint LwaAlpha = 0x2;

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint newOwner);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32", SetLastError = true)]
    public static partial nint GetClipboardData(uint format);

    [LibraryImport("user32", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    [LibraryImport("user32")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint window, uint colourKey, byte alpha, uint flags);

    [LibraryImport("user32")]
    public static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point32 point);

    [LibraryImport("user32", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint parameter, out int value, uint update);

    /// <summary>WNDENUMPROC: nonzero goes on to the next window.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int WindowEnumProcedure(nint window, nint state);

    /// <summary>WNDPROC.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumThreadWindows(uint threadId, WindowEnumProcedure callback, nint state);

    [LibraryImport("user32", EntryPoint = "GetClassNameW", SetLastError = true)]
    public static partial int GetClassName(nint window, ref char name, int capacity);

    [LibraryImport("user32", EntryPoint = "CallWindowProcW")]
    public static partial nint CallWindowProc(nint previous, nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("shell32", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("kernel32")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32")]
    public static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredRead(string target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32")]
    public static partial void CredFree(void* buffer);

    /// <summary>POINT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point32
    {
        public int X;
        public int Y;
    }

    /// <summary>NOTIFYICONDATAW, as Windows Vista and later take it: 976 bytes on 64-bit Windows.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconData
    {
        public const int TipLength = 128;
        public const int InfoLength = 256;
        public const int InfoTitleLength = 64;

        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public TipBuffer Tip;
        public uint State;
        public uint StateMask;
        public InfoBuffer Info;
        public uint TimeoutOrVersion;
        public InfoTitleBuffer InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    /// <summary>szTip: an inline array has the layout of a fixed buffer, and needs no unsafe code.</summary>
    [InlineArray(NotifyIconData.TipLength)]
    public struct TipBuffer
    {
        private char _first;
    }

    /// <summary>szInfo.</summary>
    [InlineArray(NotifyIconData.InfoLength)]
    public struct InfoBuffer
    {
        private char _first;
    }

    /// <summary>szInfoTitle.</summary>
    [InlineArray(NotifyIconData.InfoTitleLength)]
    public struct InfoTitleBuffer
    {
        private char _first;
    }

    /// <summary>CREDENTIALW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }
}
