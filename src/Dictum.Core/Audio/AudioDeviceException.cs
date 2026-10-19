using Dictum.Core.Diagnostics;

namespace Dictum.Core.Audio;

/// <summary>The audio library, a device or a stream failed.</summary>
public sealed class AudioDeviceException(string message, string? hint = null) : Exception(message), IHasHint
{
    public string? Hint { get; } = hint;
}
