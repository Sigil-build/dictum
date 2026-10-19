using System.Security.Cryptography;
using System.Text;
using Dictum.Core;

namespace Dictum.Host.Ipc;

/// <summary>
/// Where the daemon listens: a Unix domain socket on every OS (Windows 10 1803+ has AF_UNIX).
/// <c>DICTUM_SOCKET</c> overrides the path.
/// </summary>
public static class IpcEndpoint
{
    private const string SocketFileName = "dictum.sock";

    /// <summary>The OS limit is 108 bytes, 104 on macOS; this leaves a margin below both.</summary>
    private const int MaxSocketPathBytes = 100;

    /// <summary>Hex digits of the path hash in a shortened socket name: 48 bits keep data directories apart.</summary>
    private const int ShortHashLength = 12;

    public static string SocketPath(AppPaths paths)
    {
        if (Environment.GetEnvironmentVariable(DictumEnvironment.Socket) is { Length: > 0 } custom)
        {
            return custom;
        }

        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime)
        {
            return Path.Combine(runtime, SocketFileName);
        }

        // macOS and Linux without XDG_RUNTIME_DIR use the per-user data directory; Windows uses %LOCALAPPDATA%\Dictum.
        return Shorten(Path.Combine(paths.DataDirectory, SocketFileName));
    }

    /// <summary>
    /// A path over the socket path limit is replaced by a short one in the temp directory, derived from the original so
    /// each data directory keeps its own socket.
    /// </summary>
    public static string Shorten(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) <= MaxSocketPathBytes)
        {
            return path;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..ShortHashLength];
        return Path.Combine(Path.GetTempPath(), $"dictum-{hash}.sock");
    }
}
