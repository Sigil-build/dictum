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

    private const int StderrFd = 2;

    /// <summary><c>O_WRONLY</c>, the same on Linux and macOS.</summary>
    private const int OpenWriteOnly = 1;

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
        var saved = LibCNative.dup(StderrFd);
        var devNull = LibCNative.open("/dev/null", OpenWriteOnly);
        if (saved < 0 || devNull < 0)
        {
            CloseIfOpen(saved);
            CloseIfOpen(devNull);
            return action();
        }

        // Best effort: when a redirect or close fails, the native output stays visible and nothing else changes.
        try
        {
            _ = LibCNative.dup2(devNull, StderrFd);
            return action();
        }
        finally
        {
            _ = LibCNative.dup2(saved, StderrFd);
            _ = LibCNative.close(saved);
            _ = LibCNative.close(devNull);
        }
    }

    private static void CloseIfOpen(int fd)
    {
        if (fd >= 0)
        {
            _ = LibCNative.close(fd);
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
