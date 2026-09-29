namespace Dictum.Core.Tests.Audio;

internal static class Signals
{
    public static float[] Sine(double frequency, int sampleRate, double seconds, float amplitude = 0.5f)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        }

        return samples;
    }

    public static float[] Silence(int sampleRate, double seconds) => new float[(int)(sampleRate * seconds)];

    /// <summary>Estimates the dominant frequency by counting rising zero crossings.</summary>
    public static double Frequency(ReadOnlySpan<float> samples, int sampleRate)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                crossings++;
            }
        }

        return crossings * (double)sampleRate / samples.Length;
    }
}
