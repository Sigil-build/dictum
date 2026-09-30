using Dictum.Core.Diagnostics;

namespace Dictum.Platform.Linux;

public static class LinuxPlatform
{
    public static PlatformServices Create(ILog log, Func<PlatformSettings> settings) =>
        UnsupportedPlatform.Create("Linux", "Linux hotkeys and paste are not wired up yet.");
}
