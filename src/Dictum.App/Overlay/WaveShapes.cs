namespace Dictum.App.Overlay;

/// <summary>The prototype's drawing maths (<c>drawWaves</c>) in overlay units: pure functions of x and time.</summary>
public static class WaveShapes
{
    private const double W = OverlayTokens.Width;
    private const double Cy = OverlayTokens.CenterY;

    /// <summary>The horizontal envelope that keeps both ends flat.</summary>
    public static double Envelope(double x) =>
        Math.Exp(-Math.Pow(x / W - 0.5, 2) / (2 * OverlayTokens.EnvelopeSigma * OverlayTokens.EnvelopeSigma));

    public static double BandMean(IReadOnlyList<double> bands, WaveSpec wave)
    {
        var sum = 0.0;
        for (var i = wave.FirstBand; i <= wave.LastBand; i++)
        {
            sum += i < bands.Count ? bands[i] : 0;
        }

        return sum / (wave.LastBand - wave.FirstBand + 1);
    }

    /// <summary>Wave <paramref name="index"/>'s height while the user speaks: level and bands, plus a breath that fades as the level rises.</summary>
    public static double ListeningAmplitude(int index, double level, double bandMean, double time)
    {
        var weight = OverlayTokens.Waves[index].Weight;
        var breath = OverlayTokens.BreatheBase + OverlayTokens.ListeningBreatheSwing * Math.Sin(2 * Math.PI * OverlayTokens.BreatheHz * time + index);
        return (level * OverlayTokens.AmplitudeFromLevel + bandMean * OverlayTokens.AmplitudeFromBands) * weight + breath * (1 - level) * weight;
    }

    /// <summary>Wave <paramref name="index"/>'s height in silence: a slow breath on a 4 s sine.</summary>
    public static double QuietAmplitude(int index, double time) =>
        (OverlayTokens.BreatheBase + OverlayTokens.QuietBreatheSwing * Math.Sin(2 * Math.PI * OverlayTokens.BreatheHz * time)) * OverlayTokens.Waves[index].Weight;

    /// <summary>In silence the long wave stays bright and the others dim.</summary>
    public static double QuietAlpha(int index) => index == 0 ? OverlayTokens.Waves[0].Alpha : OverlayTokens.QuietAlpha;

    public static double WaveY(int index, double x, double amplitude, double time)
    {
        var wave = OverlayTokens.Waves[index];
        return Cy + amplitude * Envelope(x) * Math.Sin(2 * Math.PI * wave.Frequency * x / W + wave.Phase + time * wave.Drift);
    }

    /// <summary>Where the Transcribing bump is, as a fraction of the width: from −0.15 to 1.15, once every 1.3 s.</summary>
    public static double BumpPosition(double time) => time % OverlayTokens.BumpPeriod + OverlayTokens.BumpStart;

    /// <summary>The Transcribing line: a small travelling ripple with the bump lifted out of it.</summary>
    public static double TranscribingY(double x, double time)
    {
        var line = Cy + OverlayTokens.TranscribingAmplitude * Envelope(x)
            * Math.Sin(2 * Math.PI * OverlayTokens.TranscribingCycles * x / W + time * OverlayTokens.TranscribingSpeed);
        var bump = Math.Exp(-Math.Pow(x / W - BumpPosition(time), 2) / (2 * OverlayTokens.BumpSigma * OverlayTokens.BumpSigma));
        return line - OverlayTokens.BumpHeight * bump;
    }

    /// <summary>The Cleaning up standing wave: three lobes on a 714 ms beat. <paramref name="ramp"/> is the 0–1 entrance.</summary>
    public static double CleaningY(double x, double time, double ramp) =>
        Cy + OverlayTokens.CleaningAmplitude * ramp * Envelope(x) * Math.Sin(3 * Math.PI * x / W) * Math.Sin(2 * Math.PI * OverlayTokens.CleaningHz * time);

    /// <summary>Walks a polyline from <paramref name="x0"/> to <paramref name="x1"/> in 4-unit steps and ends exactly at x1.</summary>
    /// <param name="point">Gets x, y and whether it is the first point.</param>
    public static void Trace(double x0, double x1, Func<double, double> y, Action<double, double, bool> point)
    {
        var x = x0;
        for (; x <= x1; x += OverlayTokens.Step)
        {
            point(x, y(x), x == x0);
        }

        if (x - OverlayTokens.Step < x1)
        {
            point(x1, y(x1), false);
        }
    }

    /// <summary>The check mark's points around its centre, drawn up to <paramref name="progress"/> of its length.</summary>
    public static IReadOnlyList<(double X, double Y)> CheckPath(double progress)
    {
        // (−7, 0) → (−2, 5) → (8, −6), with the prototype's leg lengths.
        const double first = 7.07;
        const double second = 15.62;
        var drawn = Math.Clamp(progress, 0, 1) * (first + second);
        if (drawn <= first)
        {
            var k = drawn / first;
            return [(-7, 0), (-7 + 5 * k, 5 * k)];
        }

        var j = (drawn - first) / second;
        return [(-7, 0), (-2, 5), (-2 + 10 * j, 5 - 11 * j)];
    }
}
