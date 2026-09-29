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
}
