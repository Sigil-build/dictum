using System.Text.Json.Serialization;

namespace Dictum.Core.Configuration;

/// <summary>Root of <c>config.json</c>. See the implementation plan, section 5.</summary>
/// <remarks>
/// Properties with non-default initial values use <c>set</c>, not <c>init</c>: the STJ source generator
/// assigns every init-only property in an object initializer, which would reset a missing value to null.
/// </remarks>
public sealed record DictumConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    /// <summary>Named speech-to-text providers.</summary>
    public Dictionary<string, SttConfig> Stt { get; set; } = [];

    /// <summary>Named text processors, chained by profiles.</summary>
    public Dictionary<string, ProcessorConfig> Processors { get; set; } = [];

    public List<ProfileConfig> Profiles { get; set; } = [];
}

public sealed record GeneralSettings
{
    public bool StartWithSystem { get; init; }

    public int HistorySize { get; set; } = 200;

    public bool Sounds { get; set; } = true;

    /// <summary>Microphone name, or null for the system default.</summary>
    public string? AudioDevice { get; init; }

    /// <summary>How simulated paste reaches other apps on Linux Wayland.</summary>
    public InjectorBackend Injector { get; set; } = InjectorBackend.Auto;

    /// <summary>How long to wait after the paste keystroke before restoring the previous clipboard.</summary>
    public int PasteDelayMs { get; set; } = 150;

    /// <summary>Apps (process or window class names) that paste with Ctrl+Shift+V; used where the active app is known.</summary>
    public List<string> TerminalApps { get; set; } =
    [
        "konsole", "gnome-terminal-server", "kitty", "alacritty", "org.wezfurlong.wezterm", "wezterm-gui",
        "foot", "footclient", "xterm", "tilix", "terminator", "ghostty", "com.mitchellh.ghostty", "xfce4-terminal",
    ];
}

public enum InjectorBackend
{
    /// <summary>RemoteDesktop portal, then ydotool, then wtype.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("portal")]
    Portal,

    [JsonStringEnumMemberName("ydotool")]
    Ydotool,

    [JsonStringEnumMemberName("wtype")]
    Wtype,
}

public enum PasteShortcut
{
    /// <summary>Ctrl+Shift+V for known terminals, Ctrl+V otherwise.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("ctrl+v")]
    CtrlV,

    [JsonStringEnumMemberName("ctrl+shift+v")]
    CtrlShiftV,
}

public enum HotkeyMode
{
    PushToTalk,
    Toggle,
}

public enum OutputMode
{
    /// <summary>Clipboard + simulated paste, restoring the clipboard afterwards.</summary>
    [JsonStringEnumMemberName("paste")]
    Paste,

    /// <summary>Typed character by character.</summary>
    [JsonStringEnumMemberName("type")]
    Type,

    /// <summary>Only placed on the clipboard.</summary>
    [JsonStringEnumMemberName("clipboard")]
    Clipboard,
}

public sealed record ProfileConfig
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Hotkey { get; init; }

    public HotkeyMode Mode { get; set; } = HotkeyMode.PushToTalk;

    public required string Stt { get; init; }

    /// <summary>ISO 639-1 code; null or "auto" detects the language.</summary>
    public string? Language { get; init; }

    /// <summary>Vocabulary hint for the STT model.</summary>
    public string? SttPrompt { get; init; }

    public List<string> Pipeline { get; set; } = [];

    public OutputMode Output { get; set; } = OutputMode.Paste;

    /// <summary>On Wayland the active app is unknown, so terminal profiles set this explicitly.</summary>
    public PasteShortcut PasteShortcut { get; set; } = PasteShortcut.Auto;
}
