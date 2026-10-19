namespace Dictum.Host;

/// <summary>Environment variables that redirect the host, for tests and scripted runs.</summary>
internal static class EnvironmentVariables
{
    /// <summary>A socket path that replaces the default one.</summary>
    public const string Socket = "DICTUM_SOCKET";

    /// <summary>A WAV file that stands in for the microphone.</summary>
    public const string AudioFile = "DICTUM_AUDIO_FILE";
}
