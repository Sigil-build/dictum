using System.Globalization;

namespace Dictum.Setup;

/// <param name="GpuName">The hardware GPU Whisper can use, or null when only the CPU (or a software renderer) is there.</param>
/// <param name="GpuMemoryBytes">The GPU's largest device-local heap; shared system memory for an integrated GPU; null when unknown.</param>
public sealed record HardwareInfo(string? GpuName, long FreeDiskBytes, long? GpuMemoryBytes = null, bool GpuIsIntegrated = false);

public static class HardwareText
{
    private const double BytesPerGibibyte = 1L << 30;

    /// <summary>"Intel(R) Iris(R) Xe Graphics, 15.8 GB shared memory", "RTX 3060, 12.0 GB VRAM", or null without a GPU.</summary>
    public static string? Gpu(HardwareInfo hardware) =>
        hardware.GpuName is not { } name ? null
        : hardware.GpuMemoryBytes is { } bytes and > 0 ? $"{name}, {Memory(bytes)} {(hardware.GpuIsIntegrated ? "shared memory" : "VRAM")}"
        : name;

    /// <summary>Binary gigabytes with one decimal, as Windows Task Manager labels them ("15.8 GB").</summary>
    public static string Memory(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerGibibyte:F1} GB");
}
