using System.Runtime.InteropServices;

namespace Dictum.Plugins;

/// <summary>The constants of plugin protocol 1, which a plugin's manifest and process both follow.</summary>
public static class PluginProtocol
{
    public const int Version = 1;

    /// <summary>The environment variable that carries the per-launch key to the plugin; never argv, which other users can read.</summary>
    public const string KeyVariable = "DICTUM_PLUGIN_KEY";

    public const string ManifestFile = "plugin.json";

    /// <summary>
    /// The runtime identifier a manifest's <c>executable</c> map is keyed by, such as <c>win-x64</c>: the runtime's own,
    /// or the portable one of this system when a distribution names it otherwise, as in <c>ubuntu.24.04-x64</c>.
    /// </summary>
    public static string CurrentRid { get; } = PortableRid(RuntimeInformation.RuntimeIdentifier, CurrentOs(), RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// <paramref name="runtimeRid"/> when it is already portable (<c>win-x64</c>, <c>linux-musl-arm64</c>); otherwise the
    /// portable one of the OS and architecture, such as <c>linux-x64</c>. A system Dictum does not name keeps its own.
    /// </summary>
    public static string PortableRid(string runtimeRid, OSPlatform os, Architecture architecture)
    {
        var parts = runtimeRid.Split('-');
        if ((parts.Length == 2 && parts[0] is "win" or "linux" or "osx") || (parts.Length == 3 && parts[0] == "linux" && parts[1] == "musl"))
        {
            return runtimeRid;
        }

        var family = os == OSPlatform.Windows ? "win" : os == OSPlatform.Linux ? "linux" : os == OSPlatform.OSX ? "osx" : null;
        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => null,
        };
        if (family is null || arch is null)
        {
            return runtimeRid;
        }

        // Alpine, by its name in the RID (alpine.3.20-x64), is the one distribution .NET names that uses musl.
        return family == "linux" && IsMusl(parts[0]) ? $"linux-musl-{arch}" : $"{family}-{arch}";
    }

    /// <summary>A distribution's name, such as <c>alpine</c> in <c>alpine.3.20-x64</c>, that runs on musl rather than glibc.</summary>
    private static bool IsMusl(string distribution) =>
        distribution.Split('.')[0].Equals("alpine", StringComparison.OrdinalIgnoreCase);

    private static OSPlatform CurrentOs() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Create(RuntimeInformation.RuntimeIdentifier);
}
