using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.Core.Audio;

/// <summary>One frame of a frame dump.</summary>
/// <param name="T">Seconds since the recording started, as <see cref="AudioFrame.AtSeconds"/>.</param>
public sealed record RecordedFrame(double T, float Rms, float[] Bands)
{
    public static RecordedFrame From(AudioFrame frame) => new(frame.AtSeconds, frame.Rms, frame.Bands.ToArray());
}

/// <summary>
/// The frames of one recording, as <c>dictum record --dump-frames</c> writes them. The overlay tests and
/// <c>dictum-gui --overlay-demo</c> play them by frame index.
/// </summary>
public sealed record FrameRecording(int FramesPerSecond, int Bands, IReadOnlyList<RecordedFrame> Frames)
{
    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames.Count / FramesPerSecond);

    /// <summary>The frames as the capture delivered them, at <see cref="AudioFrame.PerSecond"/>.</summary>
    public static FrameRecording Of(IReadOnlyList<RecordedFrame> frames) =>
        new(AudioFrame.PerSecond, frames.Count > 0 ? frames[0].Bands.Length : 0, frames);

    /// <summary>The frame that plays <paramref name="elapsed"/> after the start; null before it and past the end.</summary>
    public RecordedFrame? At(TimeSpan elapsed)
    {
        var index = (int)Math.Floor(elapsed.TotalSeconds * FramesPerSecond);
        return index >= 0 && index < Frames.Count ? Frames[index] : null;
    }

    public string ToJson() => JsonSerializer.Serialize(this, FrameRecordingJsonContext.Default.FrameRecording);

    /// <exception cref="FormatException">The text is not a frames file.</exception>
    public static FrameRecording Parse(string json)
    {
        FrameRecording? recording;
        try
        {
            recording = JsonSerializer.Deserialize(json, FrameRecordingJsonContext.Default.FrameRecording);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a frames file: {ex.Message}", ex);
        }

        if (recording is not { FramesPerSecond: > 0, Frames.Count: > 0 })
        {
            throw new FormatException("Not a frames file: it needs framesPerSecond and at least one frame.");
        }

        return recording;
    }

    /// <exception cref="FormatException">The file is not a frames file.</exception>
    public static FrameRecording Load(string path) => Parse(File.ReadAllText(path));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FrameRecording))]
internal sealed partial class FrameRecordingJsonContext : JsonSerializerContext;
