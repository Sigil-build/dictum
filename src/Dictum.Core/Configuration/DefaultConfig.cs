namespace Dictum.Core.Configuration;

public static class DefaultConfig
{
    public const string LocalSttId = "local";
    /// <summary>
    /// The file of the catalog's CPU model, whisper-small-q8, which <c>dictum config init</c> tells you to pull. Core cannot
    /// read the catalog, so a test keeps the two in step.
    /// </summary>
    public const string DefaultModel = "ggml-small-q8_0.bin";

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
