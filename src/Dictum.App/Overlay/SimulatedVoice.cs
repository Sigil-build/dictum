namespace Dictum.App.Overlay;

/// <summary>
/// A simulated voice for the overlay previews. Each <see cref="Cycle"/> holds <see cref="Speaking"/> seconds of
/// "speech" made of two syllable sines with short ramps in and out, then near silence. The bands come from three
/// formants with a flicker.
/// </summary>
public static class SimulatedVoice
{
    public const double Cycle = 3.4;
    public const double Speaking = 2.3;

    // The loudness: a fast and a slow syllable sine (Hz), under a slower swell (rad/s).
    private const double FastSyllableHz = 3.7;
    private const double FastSyllableBase = 0.55;
    private const double FastSyllableSwing = 0.45;
    private const double SlowSyllableHz = 1.3;
    private const double SlowSyllablePhase = 1;
    private const double SlowSyllableBase = 0.5;
    private const double SlowSyllableSwing = 0.5;
    private const double SwellRate = 0.9;
    private const double SwellBase = 0.6;
    private const double SwellSwing = 0.4;

    /// <summary>Seconds the speech takes to rise at the start of a cycle.</summary>
    private const double RampIn = 0.12;

    /// <summary>Seconds the speech takes to fall before the silence.</summary>
    private const double RampOut = 0.2;

    // The flicker on every band: two sines, in rad/s plus a phase per band.
    private const double FlickerBase = 0.75;
    private const double FlickerSwing = 0.25;
    private const double FlickerFastRate = 23;
    private const double FlickerFastPhasePerBand = 1.7;
    private const double FlickerSlowRate = 7;

    private const double BandGain = 0.65;

    /// <summary>The rms between phrases: near silence, never digital zero.</summary>
    private const double RmsFloor = 0.0005;

    private const double RmsGain = 0.035;

    /// <summary>The formants over the band index scaled to 0–1: a Gaussian bump each, with its centre, spread and gain.</summary>
    private static readonly (double Centre, double Spread, double Gain)[] Formants = [(0.12, 0.03, 1), (0.35, 0.05, 0.55), (0.7, 0.08, 0.25)];

    /// <summary>The rms at <paramref name="t"/> seconds; writes the bands into <paramref name="bands"/>.</summary>
    public static float Sample(double t, Span<float> bands)
    {
        var p = t % Cycle;
        var envelope = 0.0;
        if (p < Speaking)
        {
            var syllable = FastSyllableBase + FastSyllableSwing * Math.Sin(t * 2 * Math.PI * FastSyllableHz);
            var syllable2 = SlowSyllableBase + SlowSyllableSwing * Math.Sin(t * 2 * Math.PI * SlowSyllableHz + SlowSyllablePhase);
            envelope = Math.Max(0, syllable * syllable2 * (SwellBase + SwellSwing * Math.Sin(t * SwellRate)));
            envelope *= Math.Min(1, Math.Min(p / RampIn, (Speaking - p) / RampOut));
        }

        for (var i = 0; i < bands.Length; i++)
        {
            var f = i / (double)(OverlayTokens.BandCount - 1);
            var formant = 0.0;
            foreach (var (centre, spread, gain) in Formants)
            {
                formant += gain * Math.Exp(-Math.Pow(f - centre, 2) / spread);
            }

            var flicker = FlickerBase + FlickerSwing * Math.Sin(t * FlickerFastRate + i * FlickerFastPhasePerBand) * Math.Sin(t * FlickerSlowRate + i);
            bands[i] = (float)Math.Min(1, envelope * BandGain * formant * flicker);
        }

        return (float)(RmsFloor + envelope * RmsGain);
    }
}
