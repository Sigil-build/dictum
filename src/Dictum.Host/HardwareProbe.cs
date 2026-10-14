using Dictum.Core;
using Dictum.Setup;
using Dictum.Stt.Whisper;

namespace Dictum.Host;

/// <summary>What <c>dictum init</c> needs to know about this machine.</summary>
public static class HardwareProbe
{
    public static HardwareInfo Detect(AppPaths paths) => new(
        VulkanProbe.Devices.FirstOrDefault(d => d.IsHardware)?.Name,
        FreeBytes(paths.DataDirectory));

    /// <summary>Free space on the volume that holds <paramref name="directory"/>; unknown counts as plenty.</summary>
    private static long FreeBytes(string directory)
    {
        try
        {
            var existing = new DirectoryInfo(Path.GetFullPath(directory));
            while (!existing.Exists && existing.Parent is not null)
            {
                existing = existing.Parent;
            }

            var drive = OperatingSystem.IsWindows() ? Path.GetPathRoot(existing.FullName)! : existing.FullName;
            return new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }
}
