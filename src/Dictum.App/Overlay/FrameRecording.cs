using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.App.Overlay;

/// <summary>One 20 ms frame of a <c>dictum record --dump-frames</c> file.</summary>
public sealed record RecordedFrame(double T, float Rms, float[] Bands);

/// <summary>A <c>--dump-frames</c> recording. The overlay tests and <c>--overlay-demo</c> play it by index, as the prototype does.</summary>
public sealed record FrameRecording(int FramesPerSecond, int Bands, IReadOnlyList<RecordedFrame> Frames)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames.Count / FramesPerSecond);

    /// <summary>The frame that plays <paramref name="elapsed"/> after the start; null before it and past the end.</summary>
    public RecordedFrame? At(TimeSpan elapsed)
    {
        var index = (int)Math.Floor(elapsed.TotalSeconds * FramesPerSecond);
        return index >= 0 && index < Frames.Count ? Frames[index] : null;
    }

    /// <exception cref="FormatException">The text is not a frames file.</exception>
    public static FrameRecording Parse(string json)
    {
        FrameRecording? recording;
        try
        {
            recording = JsonSerializer.Deserialize(json, OverlayJsonContext.Default.FrameRecording);
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FrameRecording))]
internal sealed partial class OverlayJsonContext : JsonSerializerContext;
