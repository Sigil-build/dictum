namespace Dictum.Platform.Linux;

/// <summary>Session environment variables read in more than one place, and the XDG config directory they point to.</summary>
public static class LinuxEnvironment
{
    public const string Display = "DISPLAY";
    public const string WaylandDisplay = "WAYLAND_DISPLAY";
    public const string CurrentDesktop = "XDG_CURRENT_DESKTOP";
    public const string RuntimeDirectory = "XDG_RUNTIME_DIR";
    public const string ConfigHome = "XDG_CONFIG_HOME";
    public const string Home = "HOME";
    public const string DefaultConfigFolder = ".config";

    /// <summary>XDG_CONFIG_HOME, or ~/.config when it is unset or empty.</summary>
    public static string ConfigDirectory(Func<string, string?> environment) =>
        environment(ConfigHome) is { Length: > 0 } custom ? custom : Path.Combine(environment(Home) ?? "", DefaultConfigFolder);
}
