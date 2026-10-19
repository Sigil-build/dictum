using Dictum.Core.Audio;

namespace Dictum.Audio;

public static class Levels
{
    /// <summary>Floor used instead of -∞ for digital silence.</summary>
    public const float SilenceDb = -100f;

    /// <summary>The length of the frames levels are measured over.</summary>
    public const int FrameMs = 20;

    /// <summary>The RMS of <see cref="SilenceDb"/>: 20·log10(1e-5) = -100 dB.</summary>
    private const float SilenceRms = 1e-5f;

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

    public static float ToDb(float rms) => rms <= SilenceRms ? SilenceDb : 20f * MathF.Log10(rms);

    /// <summary>The level of the loudest <paramref name="frameMs"/> stretch, in dBFS.</summary>
    public static float LoudestFrameDb(ReadOnlySpan<float> samples, int sampleRate, int frameMs = FrameMs)
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
    /// <param name="relativeDb">Frames quieter than the loudest frame minus this are treated as silence.</param>
    public static AudioClip Trim(AudioClip clip, float absoluteDb = -50f, float relativeDb = 40f, int paddingMs = 250)
    {
        var samples = clip.Samples.Span;
        var frame = clip.SampleRate * Levels.FrameMs / 1000;
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

    /// <summary>Per louder frame, the noise floor rises by this share of the gap, so speech barely lifts it.</summary>
    private const float FloorRiseRate = 0.002f;

    private float _floor = float.NaN;
    private bool _heardSpeech;
    private TimeSpan _quietFor;

    public bool HeardSpeech => _heardSpeech;

    /// <summary>Feeds one frame; returns true when the speaker has stopped.</summary>
    public bool Push(float rms, TimeSpan frameDuration)
    {
        var db = Levels.ToDb(rms);

        // Noise floor: follows quiet frames quickly and loud frames very slowly.
        _floor = float.IsNaN(_floor) ? db : db < _floor ? db : _floor + (FloorRiseRate * (db - _floor));

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
    private const float MinGainDb = 0.5f;

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
        if (gainDb < MinGainDb)
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
