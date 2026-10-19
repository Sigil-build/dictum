namespace Dictum.Platform.Linux;

public enum SessionType
{
    Wayland,
    X11,
    Unknown,
}

public enum Desktop
{
    Kde,
    Gnome,
    Hyprland,
    Sway,
    Other,
}

/// <summary>What kind of graphical session the daemon runs in, from the standard environment variables.</summary>
public sealed record SessionInfo(SessionType Type, Desktop Desktop, string DesktopName)
{
    /// <summary>wlroots compositors support the virtual-keyboard protocol that wtype needs.</summary>
    public bool IsWlroots => Desktop is Desktop.Hyprland or Desktop.Sway;

    public static SessionInfo Detect() => Detect(Environment.GetEnvironmentVariable);

    public static SessionInfo Detect(Func<string, string?> env)
    {
        var sessionType = env("XDG_SESSION_TYPE");
        var type = sessionType == "wayland" || !string.IsNullOrEmpty(env("WAYLAND_DISPLAY")) ? SessionType.Wayland
            : sessionType == "x11" || !string.IsNullOrEmpty(env(LinuxEnvironment.Display)) ? SessionType.X11
            : SessionType.Unknown;

        var names = (env("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        var desktop = names.Any(n => n.Equals("KDE", StringComparison.OrdinalIgnoreCase)) ? Desktop.Kde
            : names.Any(n => n.Contains("GNOME", StringComparison.OrdinalIgnoreCase)) ? Desktop.Gnome
            : names.Any(n => n.Equals("Hyprland", StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrEmpty(env("HYPRLAND_INSTANCE_SIGNATURE")) ? Desktop.Hyprland
            : names.Any(n => n.Equals("sway", StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrEmpty(env("SWAYSOCK")) ? Desktop.Sway
            : Desktop.Other;

        return new SessionInfo(type, desktop, names.Length > 0 ? string.Join(':', names) : "unknown");
    }

    public override string ToString() => $"{Type}, {DesktopName}";
}
