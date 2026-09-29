using System.Buffers.Binary;
using Dictum.Core.Audio;

namespace Dictum.Audio;

/// <summary>Decoded WAV audio, downmixed to mono.</summary>
public sealed record WavAudio(float[] Samples, int SampleRate, int Channels, int BitsPerSample);

public sealed class WavFormatException(string message) : Exception(message);

public static class Wav
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Reads PCM (8/16/24/32-bit) or IEEE float (32/64-bit) WAV of any rate and channel count.</summary>
    public static WavAudio Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    public static WavAudio Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
        {
            throw new WavFormatException("Not a WAV file (missing RIFF/WAVE header). Convert it first, e.g. 'ffmpeg -i in.mp3 -ar 16000 -ac 1 out.wav'.");
        }

        ushort format = 0, channels = 0, bits = 0;
        var sampleRate = 0;
        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = data.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var body = offset + 8;

            if (id.SequenceEqual("fmt "u8))
            {
                var fmt = data.Slice(body, (int)Math.Min(size, (uint)(data.Length - body)));
                if (fmt.Length < 16)
                {
                    throw new WavFormatException("WAV 'fmt ' chunk is too short.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible && fmt.Length >= 26)
                {
                    // The first two bytes of the sub-format GUID carry the actual format code.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new WavFormatException("WAV 'data' chunk comes before 'fmt '.");
                }

                // Streamed WAVs (e.g. from a pipe) may carry 0 or 0xFFFFFFFF as the data size.
                var available = data.Length - body;
                var length = size == 0 || size > available ? available : (int)size;
                var samples = Decode(data.Slice(body, length), format, channels, bits);
                return new WavAudio(samples, sampleRate, channels, bits);
            }

            offset = body + (int)Math.Min(size + (size & 1), int.MaxValue - body);
        }

        throw new WavFormatException("WAV file has no 'data' chunk.");
    }

    /// <summary>Reads a WAV and converts it to a 16 kHz mono clip.</summary>
    public static AudioClip ReadClip(Stream stream)
    {
        var wav = Read(stream);
        return new AudioClip(Resampler.Resample(wav.Samples, wav.SampleRate, AudioClip.WhisperSampleRate));
    }

    /// <summary>Writes mono 16-bit PCM.</summary>
    public static void WritePcm16(Stream stream, ReadOnlySpan<float> samples, int sampleRate)
    {
        var dataBytes = samples.Length * 2;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);

        var pcm = new byte[dataBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        }

        stream.Write(pcm);
    }

    public static byte[] ToPcm16Bytes(AudioClip clip)
    {
        using var stream = new MemoryStream(44 + (clip.Samples.Length * 2));
        WritePcm16(stream, clip.Samples.Span, clip.SampleRate);
        return stream.ToArray();
    }

    private static float[] Decode(ReadOnlySpan<byte> data, ushort format, int channels, int bits)
    {
        var bytesPerSample = bits / 8;
        if (bytesPerSample == 0 || channels == 0)
        {
            throw new WavFormatException($"Unsupported WAV layout: {channels} channels, {bits} bits.");
        }

        Func<ReadOnlySpan<byte>, float> read = (format, bits) switch
        {
            (FormatPcm, 8) => b => (b[0] - 128) / 128f,
            (FormatPcm, 16) => b => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
            (FormatPcm, 24) => b => ((b[2] << 24) | (b[1] << 16) | (b[0] << 8)) / 2147483648f,
            (FormatPcm, 32) => b => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
            (FormatFloat, 32) => BinaryPrimitives.ReadSingleLittleEndian,
            (FormatFloat, 64) => b => (float)BinaryPrimitives.ReadDoubleLittleEndian(b),
            _ => throw new WavFormatException($"Unsupported WAV encoding: format {format}, {bits} bits. Use PCM or IEEE float."),
        };

        var frameBytes = bytesPerSample * channels;
        var frames = data.Length / frameBytes;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += read(data.Slice((f * frameBytes) + (c * bytesPerSample), bytesPerSample));
            }

            mono[f] = sum / channels;
        }

        return mono;
    }
}
