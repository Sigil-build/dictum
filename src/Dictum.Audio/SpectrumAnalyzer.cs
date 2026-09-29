using System.Numerics;

namespace Dictum.Audio;

/// <summary>Turns a short block of samples into log-spaced frequency bands in [0, 1] for level displays.</summary>
public sealed class SpectrumAnalyzer
{
    private const float MinFrequency = 80f;
    private const float MaxFrequency = 8_000f;
    private const float FloorDb = -80f;
    private const float CeilingDb = -10f;

    private readonly int _sampleRate;
    private readonly int _bandCount;
    private readonly int _fftSize;
    private readonly float[] _window;
    private readonly int[] _bandEdges;
    private readonly Complex[] _buffer;

    public SpectrumAnalyzer(int sampleRate, int blockSize, int bandCount = 24)
    {
        _sampleRate = sampleRate;
        _bandCount = bandCount;
        _fftSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(blockSize, 64));
        _buffer = new Complex[_fftSize];

        _window = new float[blockSize];
        for (var i = 0; i < blockSize; i++)
        {
            _window[i] = 0.5f - (0.5f * MathF.Cos(2 * MathF.PI * i / Math.Max(1, blockSize - 1)));
        }

        var top = Math.Min(MaxFrequency, sampleRate / 2f);
        _bandEdges = new int[bandCount + 1];
        for (var b = 0; b <= bandCount; b++)
        {
            var frequency = MinFrequency * MathF.Pow(top / MinFrequency, (float)b / bandCount);
            _bandEdges[b] = Math.Clamp((int)MathF.Round(frequency * _fftSize / sampleRate), 1, _fftSize / 2);
        }
    }

    public int SampleRate => _sampleRate;

    public float[] Analyze(ReadOnlySpan<float> block)
    {
        var count = Math.Min(block.Length, _window.Length);
        for (var i = 0; i < _fftSize; i++)
        {
            _buffer[i] = i < count ? new Complex(block[i] * _window[i], 0) : Complex.Zero;
        }

        Fft(_buffer);

        var bands = new float[_bandCount];
        var scale = 2.0 / Math.Max(1, count);
        for (var b = 0; b < _bandCount; b++)
        {
            var from = _bandEdges[b];
            var to = Math.Max(from + 1, _bandEdges[b + 1]);
            double peak = 0;
            for (var k = from; k < to && k <= _fftSize / 2; k++)
            {
                peak = Math.Max(peak, _buffer[k].Magnitude * scale);
            }

            var db = peak <= 1e-9 ? FloorDb : 20f * (float)Math.Log10(peak);
            bands[b] = Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0f, 1f);
        }

        return bands;
    }

    /// <summary>In-place iterative radix-2 FFT; the length must be a power of two.</summary>
    internal static void Fft(Span<Complex> data)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var unit = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var even = data[start + k];
                    var odd = data[start + k + (length / 2)] * w;
                    data[start + k] = even + odd;
                    data[start + k + (length / 2)] = even - odd;
                    w *= unit;
                }
            }
        }
    }
}
