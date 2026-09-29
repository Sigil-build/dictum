using System.Buffers.Binary;
using Dictum.Audio;
using Dictum.Core.Audio;

namespace Dictum.Core.Tests.Audio;

public class WavTests
{
    [Fact]
    public void Pcm16RoundTripsWithinQuantisationError()
    {
        var samples = Signals.Sine(440, 16_000, 0.1);
        using var stream = new MemoryStream();

        Wav.WritePcm16(stream, samples, 16_000);
        stream.Position = 0;
        var wav = Wav.Read(stream);

        Assert.Equal(16_000, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.Equal(16, wav.BitsPerSample);
        Assert.Equal(samples.Length, wav.Samples.Length);
        Assert.All(samples.Zip(wav.Samples), p => Assert.Equal(p.First, p.Second, 0.0001f));
    }

    [Fact]
    public void StereoFloatIsDownmixed()
    {
        var bytes = BuildWav(format: 3, channels: 2, bits: 32, rate: 44_100, frames: [[0.5f, -0.5f], [1f, 0.5f]]);

        var wav = Wav.Read(bytes);

        Assert.Equal(44_100, wav.SampleRate);
        Assert.Equal([0f, 0.75f], wav.Samples);
    }

    [Fact]
    public void Pcm24AndExtensibleHeadersAreSupported()
    {
        var bytes = BuildWav(format: 1, channels: 1, bits: 24, rate: 8_000, frames: [[0.5f], [-0.25f]], extensible: true);

        var wav = Wav.Read(bytes);

        Assert.Equal(0.5f, wav.Samples[0], 0.0001f);
        Assert.Equal(-0.25f, wav.Samples[1], 0.0001f);
    }

    [Fact]
    public void StreamedDataSizeReadsToTheEnd()
    {
        var bytes = BuildWav(format: 1, channels: 1, bits: 16, rate: 16_000, frames: [[0.1f], [0.2f], [0.3f]]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6 - 4), 0xFFFFFFFF);

        Assert.Equal(3, Wav.Read(bytes).Samples.Length);
    }

    [Fact]
    public void ReadClipResamplesTo16Khz()
    {
        using var stream = new MemoryStream();
        Wav.WritePcm16(stream, Signals.Sine(440, 48_000, 1.0), 48_000);
        stream.Position = 0;

        var clip = Wav.ReadClip(stream);

        Assert.Equal(AudioClip.WhisperSampleRate, clip.SampleRate);
        Assert.Equal(1.0, clip.Duration.TotalSeconds, 0.01);
    }

    [Fact]
    public void NonWavInputExplainsHowToConvert()
    {
        var ex = Assert.Throws<WavFormatException>(() => Wav.Read("ID3\u0004 not a wav"u8));

        Assert.Contains("ffmpeg", ex.Message);
    }

    private static byte[] BuildWav(ushort format, ushort channels, ushort bits, int rate, float[][] frames, bool extensible = false)
    {
        using var data = new MemoryStream();
        var b = new byte[bits / 8];
        foreach (var frame in frames)
        {
            foreach (var sample in frame)
            {
                switch (format, bits)
                {
                    case (3, 32):
                        BinaryPrimitives.WriteSingleLittleEndian(b, sample);
                        break;
                    case (1, 16):
                        BinaryPrimitives.WriteInt16LittleEndian(b, (short)(sample * 32767));
                        break;
                    case (1, 24):
                        var v = (int)(sample * 8388607);
                        b[0] = (byte)v;
                        b[1] = (byte)(v >> 8);
                        b[2] = (byte)(v >> 16);
                        break;
                }

                data.Write(b);
            }
        }

        var fmtSize = extensible ? 40 : 16;
        using var wav = new MemoryStream();
        var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + fmtSize + 8 + (int)data.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(fmtSize);
        writer.Write(extensible ? (ushort)0xFFFE : format);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8));
        writer.Write(bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write(bits);
            writer.Write(0);
            writer.Write(format);
            writer.Write(new byte[14]);
        }

        writer.Write("data"u8);
        writer.Write((int)data.Length);
        writer.Write(data.ToArray());
        return wav.ToArray();
    }
}
