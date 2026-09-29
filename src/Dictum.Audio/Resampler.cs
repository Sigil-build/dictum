namespace Dictum.Audio;

/// <summary>Band-limited (windowed-sinc) resampling for arbitrary rate ratios.</summary>
public static class Resampler
{
    /// <summary>Zero crossings of the sinc kernel on each side of the centre.</summary>
    private const int HalfTaps = 16;

    /// <summary>Keeps the cutoff a little below Nyquist so the transition band is not aliased.</summary>
    private const double CutoffMargin = 0.95;

    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);
        if (fromRate == toRate || input.IsEmpty)
        {
            return input.ToArray();
        }

        var ratio = (double)toRate / fromRate;
        var step = 1.0 / ratio;
        var cutoff = Math.Min(1.0, ratio) * CutoffMargin;
        var radius = HalfTaps / cutoff;
        var output = new float[(int)Math.Floor(input.Length * ratio)];

        for (var i = 0; i < output.Length; i++)
        {
            var centre = i * step;
            var first = Math.Max(0, (int)Math.Ceiling(centre - radius));
            var last = Math.Min(input.Length - 1, (int)Math.Floor(centre + radius));
            double sum = 0, weights = 0;
            for (var j = first; j <= last; j++)
            {
                var distance = j - centre;
                var weight = Sinc(cutoff * distance) * Blackman(distance / radius);
                sum += input[j] * weight;
                weights += weight;
            }

            // Normalising by the kernel sum keeps unity gain at DC and near the edges.
            output[i] = weights == 0 ? 0 : (float)(sum / weights);
        }

        return output;
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    /// <summary>Blackman window over t ∈ [-1, 1].</summary>
    private static double Blackman(double t) =>
        Math.Abs(t) >= 1 ? 0 : 0.42 + (0.5 * Math.Cos(Math.PI * t)) + (0.08 * Math.Cos(2 * Math.PI * t));
}
