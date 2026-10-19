namespace Dictum.Core;

/// <summary>The environment variables that redirect Dictum's files, socket and microphone, for tests and scripted runs.</summary>
public static class DictumEnvironment
{
    /// <summary>A config file that replaces the default one; <c>--config</c> wins over it.</summary>
    public const string ConfigFile = "DICTUM_CONFIG";

    public const string ConfigDirectory = "DICTUM_CONFIG_DIR";

    public const string DataDirectory = "DICTUM_DATA_DIR";

    /// <summary>A socket path that replaces the default one.</summary>
    public const string Socket = "DICTUM_SOCKET";

    /// <summary>A WAV file that stands in for the microphone.</summary>
    public const string AudioFile = "DICTUM_AUDIO_FILE";
}
