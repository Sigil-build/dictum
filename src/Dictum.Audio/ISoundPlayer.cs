namespace Dictum.Audio;

public enum SoundCue
{
    Start,
    Stop,
    Error,
}

/// <summary>Plays short state cues; abstracted so the daemon can be tested without audio.</summary>
public interface ISoundPlayer
{
    Task PlayAsync(SoundCue cue, CancellationToken ct);
}
