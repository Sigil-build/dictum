using System.Runtime.InteropServices;

namespace Dictum.Plugins;

/// <summary>The constants of plugin protocol 1, which a plugin's manifest and process both follow.</summary>
public static class PluginProtocol
{
    public const int Version = 1;

    /// <summary>The environment variable that carries the per-launch key to the plugin; never argv, which other users can read.</summary>
    public const string KeyVariable = "DICTUM_PLUGIN_KEY";

    public const string ManifestFile = "plugin.json";

    /// <summary>The runtime identifier a manifest's <c>executable</c> map is keyed by, such as <c>win-x64</c>.</summary>
    public static string CurrentRid => RuntimeInformation.RuntimeIdentifier;
}
