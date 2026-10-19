namespace Dictum.Platform;

/// <summary>
/// The names of the <see cref="PlatformCheck"/> rows that doctor and each platform produce. The desktop app translates
/// the rows by these names, so a renamed check shows its raw name there.
/// </summary>
public static class PlatformCheckNames
{
    public const string Config = "config";
    public const string ConfigWarnings = "config warnings";
    public const string Models = "models";
    public const string Gpu = "gpu";
    public const string AudioLibrary = "audio library";
    public const string Microphone = "microphone";
    public const string Daemon = "daemon";

    public const string Platform = "platform";
    public const string Session = "session";
    public const string Hotkeys = "hotkeys";
    public const string Paste = "paste";
    public const string PastePortal = "paste (portal)";
    public const string PasteYdotool = "paste (ydotool)";
    public const string PasteOrder = "paste order";
    public const string Clipboard = "clipboard";
    public const string ActiveWindow = "active window";
    public const string Secrets = "secrets";
    public const string Elevation = "elevation";

    /// <summary>Followed by the id of a configured STT provider.</summary>
    public const string SttPrefix = "stt:";

    /// <summary>Followed by the id of a configured processor.</summary>
    public const string ProcessorPrefix = "processor:";

    public static string Stt(string id) => SttPrefix + id;

    public static string Processor(string id) => ProcessorPrefix + id;
}
