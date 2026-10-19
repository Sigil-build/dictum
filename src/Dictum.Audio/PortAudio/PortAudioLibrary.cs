using System.Reflection;
using System.Runtime.InteropServices;

namespace Dictum.Audio.PortAudio;

public sealed class AudioDeviceException(string message, string? hint = null) : Exception(message)
{
    /// <summary>How to fix it, shown apart from the message.</summary>
    public string? Hint { get; } = hint;
}

/// <summary>
/// Loads and initialises PortAudio once per process.
/// </summary>
/// <remarks>
/// On Linux the system library is preferred: the bundled build links libjack.so.0, which is not
/// installed everywhere. <c>DICTUM_PORTAUDIO_LIB</c> overrides the choice with an explicit path.
/// </remarks>
public static class PortAudioLibrary
{
    private static readonly Lock Gate = new();
    private static readonly Lock ResolverGate = new();
    private static bool _resolverInstalled;
    private static bool _initialized;

    /// <summary>What was loaded, e.g. "system libportaudio.so.2"; null before the first load.</summary>
    public static string? LoadedFrom { get; private set; }

    public static string? Version { get; private set; }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            EnsureResolver();
            int error;
            try
            {
                error = NativeStderr.Silence(PortAudioNative.Pa_Initialize);
            }
            catch (DllNotFoundException ex)
            {
                throw new AudioDeviceException($"PortAudio could not be loaded: {MissingLibraries(ex.Message)}", InstallHint());
            }

            if (error != PortAudioNative.NoError)
            {
                throw new AudioDeviceException($"PortAudio failed to initialise: {PortAudioNative.ErrorText(error)}");
            }

            Version = Marshal.PtrToStringUTF8(PortAudioNative.Pa_GetVersionText());
            _initialized = true;
        }
    }

    public static string InstallHint() =>
        OperatingSystem.IsLinux()
            ? "Install the system package: 'sudo pacman -S portaudio' (Arch), 'sudo apt install libportaudio2' (Debian/Ubuntu) or 'sudo dnf install portaudio' (Fedora)."
            : OperatingSystem.IsMacOS()
                ? "Reinstall Dictum or run 'brew install portaudio'."
                : "Reinstall Dictum; portaudio.dll must sit next to dictum.exe.";

    /// <summary>Keeps only the "cannot open shared object" lines of a loader error.</summary>
    private static string MissingLibraries(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("cannot open", StringComparison.Ordinal) || l.Contains("not found", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return lines.Count > 0 ? string.Join("; ", lines) : message;
    }

    /// <summary>One resolver serves every native import of this assembly (PortAudio and libc).</summary>
    internal static void EnsureResolver()
    {
        lock (ResolverGate)
        {
            if (_resolverInstalled)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(PortAudioLibrary).Assembly, Resolve);
            _resolverInstalled = true;
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == NativeStderr.LibC)
        {
            // "libc.so" on Linux is a linker script, not a loadable library.
            return NativeLibrary.TryLoad(OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6", out var libc) ? libc : 0;
        }

        if (name != PortAudioNative.Library)
        {
            return 0;
        }

        foreach (var (candidate, label) in Candidates())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                LoadedFrom = label;
                return handle;
            }
        }

        // Falls back to the default probing, which also covers runtimes/<rid>/native in non-published builds.
        if (NativeLibrary.TryLoad(name, assembly, searchPath, out var fallback))
        {
            LoadedFrom = "bundled (runtime probing)";
            return fallback;
        }

        return 0;
    }

    private static IEnumerable<(string Path, string Label)> Candidates()
    {
        if (Environment.GetEnvironmentVariable("DICTUM_PORTAUDIO_LIB") is { Length: > 0 } custom)
        {
            yield return (custom, $"DICTUM_PORTAUDIO_LIB={custom}");
        }

        var baseDir = AppContext.BaseDirectory;
        if (OperatingSystem.IsLinux())
        {
            yield return ("libportaudio.so.2", "system libportaudio.so.2");
            yield return (Path.Combine(baseDir, "libportaudio.so"), "bundled libportaudio.so");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return (Path.Combine(baseDir, "libportaudio.dylib"), "bundled libportaudio.dylib");
            yield return ("libportaudio.2.dylib", "system libportaudio.2.dylib");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return (Path.Combine(baseDir, "portaudio.dll"), "bundled portaudio.dll");
        }
    }
}
