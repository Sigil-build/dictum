using Dictum.Audio;

namespace Dictum.Testing;

/// <summary>Records the cues it is asked to play, from any thread.</summary>
public sealed class FakeSounds : ISoundPlayer
{
    public List<SoundCue> Played { get; } = [];

    public Exception? Fails { get; set; }

    public Task PlayAsync(SoundCue cue, CancellationToken ct)
    {
        if (Fails is not null)
        {
            return Task.FromException(Fails);
        }

        lock (Played)
        {
            Played.Add(cue);
        }

        return Task.CompletedTask;
    }
}
