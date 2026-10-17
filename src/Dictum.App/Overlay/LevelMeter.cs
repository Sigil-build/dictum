namespace Dictum.App.Overlay;

/// <summary>
/// The prototype's level maths (spec 5.1): rms to a 0–1 level between −60 and −26 dBFS; one-pole smoothing of the level
/// and the 24 bands on every rendered frame (attack 30 ms, release 160 ms, steps capped at 50 ms); and silence once the
/// level has stayed under 0.04 for more than 500 ms.
/// </summary>
public sealed class LevelMeter
{
    private readonly double[] _bands = new double[OverlayTokens.BandCount];
    private TimeSpan _last;
    private TimeSpan? _quietSince;

    public double Level { get; private set; }

    public IReadOnlyList<double> Bands => _bands;

    public bool Quiet { get; private set; }

    public static double Target(float rms) =>
        Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-6)) - OverlayTokens.FloorDb) / (OverlayTokens.CeilingDb - OverlayTokens.FloorDb), 0, 1);

    /// <summary>Starts from silence, as the prototype does when listening starts.</summary>
    public void Reset(TimeSpan now)
    {
        Array.Clear(_bands);
        Level = 0;
        Quiet = false;
        _quietSince = null;
        _last = now;
    }

    /// <summary>One rendered frame, with the newest audio frame (or silence).</summary>
    public void Update(float rms, ReadOnlySpan<float> bands, TimeSpan now)
    {
        var dt = Math.Clamp((now - _last).TotalSeconds, 0, OverlayTokens.MaxStep);
        _last = now;
        var attack = 1 - Math.Exp(-dt / OverlayTokens.Attack);
        var release = 1 - Math.Exp(-dt / OverlayTokens.Release);
        Level = Smooth(Level, Target(rms), attack, release);
        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i] = Smooth(_bands[i], i < bands.Length ? bands[i] : 0, attack, release);
        }

        if (Level < OverlayTokens.SilenceThreshold)
        {
            _quietSince ??= now;
            Quiet = (now - _quietSince.Value).TotalSeconds > OverlayTokens.SilenceHold;
        }
        else
        {
            _quietSince = null;
            Quiet = false;
        }
    }

    private static double Smooth(double value, double target, double attack, double release) =>
        value + (target - value) * (target > value ? attack : release);
}
