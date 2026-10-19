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

    /// <summary>"RIFF", the file size, "WAVE".</summary>
    private const int RiffHeaderSize = 12;

    /// <summary>A four-byte chunk id and a four-byte size.</summary>
    private const int ChunkHeaderSize = 8;

    private const int FmtChunkSize = 16;

    /// <summary>A <c>WAVE_FORMAT_EXTENSIBLE</c> 'fmt ' chunk holds the real format code here, in its sub-format GUID.</summary>
    private const int SubFormatOffset = 24;

    /// <summary>A RIFF header, a 16-byte 'fmt ' chunk and the 'data' chunk header.</summary>
    private const int Pcm16HeaderSize = RiffHeaderSize + ChunkHeaderSize + FmtChunkSize + ChunkHeaderSize;

    private const int Pcm16BitsPerSample = 16;
    private const int Pcm16BytesPerSample = Pcm16BitsPerSample / 8;

    // Full-scale divisors that map PCM to [-1, 1): 2^7 for unsigned 8-bit around its midpoint, 2^15, and 2^31 for
    // 32-bit and for 24-bit shifted into the top of an int.
    private const float Pcm8Scale = 128f;
    private const float Pcm16Scale = 32768f;
    private const float Pcm32Scale = 2147483648f;

    private static ReadOnlySpan<byte> RiffId => "RIFF"u8;

    private static ReadOnlySpan<byte> WaveId => "WAVE"u8;

    private static ReadOnlySpan<byte> FmtId => "fmt "u8;

    private static ReadOnlySpan<byte> DataId => "data"u8;

    /// <summary>Reads PCM (8/16/24/32-bit) or IEEE float (32/64-bit) WAV of any rate and channel count.</summary>
    public static WavAudio Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    public static WavAudio Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < RiffHeaderSize || !data[..4].SequenceEqual(RiffId) || !data[8..RiffHeaderSize].SequenceEqual(WaveId))
        {
            throw new WavFormatException("Not a WAV file (missing RIFF/WAVE header). Convert it first, e.g. 'ffmpeg -i in.mp3 -ar 16000 -ac 1 out.wav'.");
        }

        ushort format = 0, channels = 0, bits = 0;
        var sampleRate = 0;
        var offset = RiffHeaderSize;
        while (offset + ChunkHeaderSize <= data.Length)
        {
            var id = data.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var body = offset + ChunkHeaderSize;

            if (id.SequenceEqual(FmtId))
            {
                var fmt = data.Slice(body, (int)Math.Min(size, (uint)(data.Length - body)));
                if (fmt.Length < FmtChunkSize)
                {
                    throw new WavFormatException("WAV 'fmt ' chunk is too short.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible && fmt.Length >= SubFormatOffset + sizeof(ushort))
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[SubFormatOffset..]);
                }
            }
            else if (id.SequenceEqual(DataId))
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

            // Chunks are padded to an even size.
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
        const ushort Mono = 1;
        var dataBytes = samples.Length * Pcm16BytesPerSample;
        Span<byte> header = stackalloc byte[Pcm16HeaderSize];
        RiffId.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Pcm16HeaderSize - ChunkHeaderSize + dataBytes);
        WaveId.CopyTo(header[8..]);
        FmtId.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], FmtChunkSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], Mono);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * Pcm16BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], Pcm16BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], Pcm16BitsPerSample);
        DataId.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);

        var pcm = new byte[dataBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * Pcm16BytesPerSample), value);
        }

        stream.Write(pcm);
    }

    public static byte[] ToPcm16Bytes(AudioClip clip)
    {
        using var stream = new MemoryStream(Pcm16HeaderSize + (clip.Samples.Length * Pcm16BytesPerSample));
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
            (FormatPcm, 8) => b => (b[0] - Pcm8Scale) / Pcm8Scale,
            (FormatPcm, 16) => b => BinaryPrimitives.ReadInt16LittleEndian(b) / Pcm16Scale,
            (FormatPcm, 24) => b => ((b[2] << 24) | (b[1] << 16) | (b[0] << 8)) / Pcm32Scale,
            (FormatPcm, 32) => b => BinaryPrimitives.ReadInt32LittleEndian(b) / Pcm32Scale,
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
