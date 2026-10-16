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

    /// <summary>The level of the loudest <paramref name="frameMs"/> stretch, in dBFS.</summary>
    public static float LoudestFrameDb(ReadOnlySpan<float> samples, int sampleRate, int frameMs = 20)
    {
        var frame = Math.Max(1, sampleRate * frameMs / 1000);
        var loudest = SilenceDb;
        for (var start = 0; start < samples.Length; start += frame)
        {
            loudest = Math.Max(loudest, ToDb(Rms(samples.Slice(start, Math.Min(frame, samples.Length - start)))));
        }

        return loudest;
    }
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

/// <summary>
/// Brings quiet recordings up to a normal level before recognition. It only ever boosts, and by a capped
/// amount, so near-silence is not blown up into loud noise.
/// </summary>
public static class GainNormalizer
{
    /// <param name="minLoudestFrameDb">
    /// A clip whose loudest 20 ms is quieter than this is an idle microphone and is left alone: boosted, a click in
    /// it would survive silence trimming and come back from Whisper as a word. Speech on a quiet headset still
    /// reaches about -37 dBFS.
    /// </param>
    public static AudioClip Normalize(AudioClip clip, float targetPeakDb = -3f, float maxGainDb = 30f, float minLoudestFrameDb = -55f)
    {
        var samples = clip.Samples.Span;
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        if (peak <= 0 || Levels.LoudestFrameDb(samples, clip.SampleRate) < minLoudestFrameDb)
        {
            return clip;
        }

        var gainDb = Math.Clamp(targetPeakDb - Levels.ToDb(peak), 0f, maxGainDb);
        if (gainDb < 0.5f)
        {
            return clip;
        }

        var gain = MathF.Pow(10f, gainDb / 20f);
        var boosted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            boosted[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        }

        return new AudioClip(boosted, clip.SampleRate);
    }
}
