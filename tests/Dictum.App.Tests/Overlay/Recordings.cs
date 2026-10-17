using Dictum.App.Overlay;

namespace Dictum.App.Tests.Overlay;

/// <summary>The design's <c>--dump-frames</c> recordings, copied next to the tests.</summary>
internal static class Recordings
{
    public static FrameRecording Load(string name) => FrameRecording.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
}
