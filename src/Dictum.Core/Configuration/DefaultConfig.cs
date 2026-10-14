namespace Dictum.Core.Configuration;

public static class DefaultConfig
{
    public const string LocalSttId = "local";
    public const string DefaultModel = "ggml-base.bin";

    /// <summary>Starting config: one local Whisper provider and one profile. <c>dictum init</c> builds on it.</summary>
    public static DictumConfig Create() => new()
    {
        Stt = new()
        {
            [LocalSttId] = new WhisperLocalSttConfig { Model = DefaultModel },
        },
        Profiles =
        [
            new ProfileConfig
            {
                Id = "dictate",
                Name = "Dictate",
                Hotkey = "Ctrl+Alt+Space",
                Stt = LocalSttId,
            },
        ],
    };
}
