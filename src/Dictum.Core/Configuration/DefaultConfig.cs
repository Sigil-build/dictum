namespace Dictum.Core.Configuration;

public static class DefaultConfig
{
    public const string LocalSttId = "local";
    public const string DefaultModel = "ggml-base.bin";

    /// <summary>Where llama-server and whisper-server listen by default, with the API's version segment.</summary>
    public const string LocalServerUrl = "http://localhost:8080/v1";

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
