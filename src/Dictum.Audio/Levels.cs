using Dictum.Core.Audio;

namespace Dictum.Audio;

public static class Levels
{
    /// <summary>Floor used instead of -∞ for digital silence.</summary>
    public const float SilenceDb = -100f;

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    public static float ToDb(float rms) => rms <= 1e-5f ? SilenceDb : 20f * MathF.Log10(rms);
}

/// <summary>Removes leading and trailing silence, keeping some padding around speech.</summary>
public static class SilenceTrimmer
{
    private const int FrameMs = 20;

    /// <param name="relativeDb">Frames quieter than the loudest frame minus this are treated as silence.</param>
    public static AudioClip Trim(AudioClip clip, float absoluteDb = -50f, float relativeDb = 40f, int paddingMs = 250)
    {
        var samples = clip.Samples.Span;
        var frame = clip.SampleRate * FrameMs / 1000;
        var frames = samples.Length / frame;
        if (frames == 0)
        {
            return clip;
        }

        var db = new float[frames];
        var loudest = Levels.SilenceDb;
        for (var f = 0; f < frames; f++)
        {
            db[f] = Levels.ToDb(Levels.Rms(samples.Slice(f * frame, frame)));
            loudest = Math.Max(loudest, db[f]);
        }

        var threshold = Math.Max(absoluteDb, loudest - relativeDb);
        var first = Array.FindIndex(db, d => d >= threshold);
        if (first < 0)
        {
            return new AudioClip(ReadOnlyMemory<float>.Empty, clip.SampleRate);
        }

        var last = Array.FindLastIndex(db, d => d >= threshold);
        var padding = clip.SampleRate * paddingMs / 1000;
        var start = Math.Max(0, (first * frame) - padding);
        var end = Math.Min(samples.Length, ((last + 1) * frame) + padding);
        return new AudioClip(clip.Samples[start..end], clip.SampleRate);
    }
}

/// <summary>
/// Streaming end-of-speech detector for <c>--until-silence</c>: fires once speech was heard and then
/// the level stayed near the noise floor for the configured time.
/// </summary>
public sealed class SilenceDetector(TimeSpan silence, float speechAboveFloorDb = 15f, float silenceAboveFloorDb = 8f)
{
    private const float MinimumSpeechDb = -55f;

    private float _floor = float.NaN;
    private bool _heardSpeech;
    private TimeSpan _quietFor;

    public bool HeardSpeech => _heardSpeech;

    /// <summary>Feeds one frame; returns true when the speaker has stopped.</summary>
    public bool Push(float rms, TimeSpan frameDuration)
    {
        var db = Levels.ToDb(rms);

        // Noise floor: follows quiet frames quickly and loud frames very slowly.
        _floor = float.IsNaN(_floor) ? db : db < _floor ? db : _floor + (0.002f * (db - _floor));

        if (db > MinimumSpeechDb && db > _floor + speechAboveFloorDb)
        {
            _heardSpeech = true;
            _quietFor = TimeSpan.Zero;
            return false;
        }

        _quietFor = db < _floor + silenceAboveFloorDb ? _quietFor + frameDuration : TimeSpan.Zero;
        return _heardSpeech && _quietFor >= silence;
    }
}
