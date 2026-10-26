using Dictum.Core;
using Dictum.Setup;
using Dictum.Stt.Whisper;

namespace Dictum.Host;

/// <summary>The GPU and free disk space that model recommendations depend on.</summary>
public static class HardwareProbe
{
    public static HardwareInfo Detect(AppPaths paths)
    {
        var gpu = VulkanProbe.Devices.FirstOrDefault(d => d.IsHardware);
        return new HardwareInfo(
            gpu?.Name,
            FreeDiskBytesAt(paths.DownloadsDirectory),
            gpu is { MemoryBytes: > 0 } ? gpu.MemoryBytes : null,
            gpu?.Type == VulkanDeviceType.IntegratedGpu);
    }

    /// <summary>Free space in the downloads folder, where the models go; unknown counts as plenty.</summary>
    public static long FreeDiskBytes(AppPaths paths) => FreeDiskBytesAt(paths.DownloadsDirectory);

    /// <summary>Free space on the drive of <paramref name="directory"/>, or of its nearest existing parent; unknown counts as plenty.</summary>
    public static long FreeDiskBytesAt(string directory)
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
