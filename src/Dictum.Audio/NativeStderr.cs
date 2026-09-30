using System.Runtime.InteropServices;
using Dictum.Audio.PortAudio;

namespace Dictum.Audio;

/// <summary>
/// Discards what native libraries write straight to file descriptor 2 while they initialise, such as
/// ALSA/JACK probing in PortAudio or Mesa's "radv is not a conformant Vulkan implementation".
/// </summary>
public static partial class NativeStderr
{
    internal const string LibC = "libc";

    /// <summary>When true (<c>--verbose</c>), native output is left alone.</summary>
    public static bool ShowDiagnostics { get; set; }

    public static T Silence<T>(Func<T> action)
    {
        if (ShowDiagnostics || OperatingSystem.IsWindows())
        {
            return action();
        }

        PortAudioLibrary.EnsureResolver();
        Console.Error.Flush();
        var saved = LibCNative.dup(2);
        var devNull = LibCNative.open("/dev/null", 1 /* O_WRONLY */);
        if (saved < 0 || devNull < 0)
        {
            return action();
        }

        try
        {
            LibCNative.dup2(devNull, 2);
            return action();
        }
        finally
        {
            LibCNative.dup2(saved, 2);
            LibCNative.close(saved);
            LibCNative.close(devNull);
        }
    }

    private static partial class LibCNative
    {
        [LibraryImport(LibC)]
        public static partial int dup(int fd);

        [LibraryImport(LibC)]
        public static partial int dup2(int oldFd, int newFd);

        [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int open(string path, int flags);

        [LibraryImport(LibC)]
        public static partial int close(int fd);
    }
}
