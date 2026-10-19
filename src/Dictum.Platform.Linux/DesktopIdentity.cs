namespace Dictum.Platform.Linux;

/// <summary>How Dictum names itself to the Linux desktop: in its .desktop files, notifications and portal calls.</summary>
internal static class DesktopIdentity
{
    /// <summary>The portal and the notification server look Dictum up as <c>&lt;AppId&gt;.desktop</c>.</summary>
    public const string AppId = "dev.dictum.Dictum";

    public const string Name = "Dictum";
    public const string Comment = "Voice to text";

    /// <summary>A standard freedesktop icon name, so every icon theme has it.</summary>
    public const string Icon = "audio-input-microphone";
}
