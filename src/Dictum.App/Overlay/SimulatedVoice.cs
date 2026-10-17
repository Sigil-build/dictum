namespace Dictum.App.Overlay;

/// <summary>
/// The prototype's simulated voice (overlay design extract §3.5). Each 3.4 s cycle holds 2.3 s of "speech" made of
/// two syllable sines with 120 ms and 200 ms ramps, then near silence. The 24 bands come from three formants with a
/// flicker. The overlay sub-page previews with it, and so will the wizard (spec 5.5).
/// </summary>
public static class SimulatedVoice
{
    public const double Cycle = 3.4;
    public const double Speaking = 2.3;

    /// <summary>The rms at <paramref name="t"/> seconds; writes the bands into <paramref name="bands"/>.</summary>
    public static float Sample(double t, Span<float> bands)
    {
        var p = t % Cycle;
        var envelope = 0.0;
        if (p < Speaking)
        {
            var syllable = 0.55 + 0.45 * Math.Sin(t * 2 * Math.PI * 3.7);
            var syllable2 = 0.5 + 0.5 * Math.Sin(t * 2 * Math.PI * 1.3 + 1);
            envelope = Math.Max(0, syllable * syllable2 * (0.6 + 0.4 * Math.Sin(t * 0.9)));
            envelope *= Math.Min(1, Math.Min(p / 0.12, (Speaking - p) / 0.2));
        }

        for (var i = 0; i < bands.Length; i++)
        {
            var f = i / 23.0;
            var formant = Math.Exp(-Math.Pow(f - 0.12, 2) / 0.03)
                + 0.55 * Math.Exp(-Math.Pow(f - 0.35, 2) / 0.05)
                + 0.25 * Math.Exp(-Math.Pow(f - 0.7, 2) / 0.08);
            var flicker = 0.75 + 0.25 * Math.Sin(t * 23 + i * 1.7) * Math.Sin(t * 7 + i);
            bands[i] = (float)Math.Min(1, envelope * 0.65 * formant * flicker);
        }

        return (float)(0.0005 + envelope * 0.035);
    }
}
