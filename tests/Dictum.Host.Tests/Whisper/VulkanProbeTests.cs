using System.Buffers.Binary;
using Dictum.Stt.Whisper;

namespace Dictum.Host.Tests.Whisper;

/// <summary>
/// VkPhysicalDeviceMemoryProperties: a type count, 32 memory types of 8 bytes, a heap count, then 16 heaps of 16 bytes
/// (size, then flags).
/// </summary>
public class VulkanProbeTests
{
    private const int PropertiesSize = 520;
    private const int HeapCountOffset = 260;
    private const int HeapsOffset = 264;
    private const int HeapStride = 16;
    private const int HeapFlagsOffset = 8;

    [Fact]
    public void TheLargestDeviceLocalHeapIsTheGpuMemory()
    {
        var properties = new byte[PropertiesSize];
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(HeapCountOffset), 3);
        Heap(properties, 0, 256L << 20, deviceLocal: true);
        Heap(properties, 1, 32L << 30, deviceLocal: false);
        Heap(properties, 2, 8L << 30, deviceLocal: true);

        Assert.Equal(8L << 30, VulkanProbe.LargestDeviceLocalHeap(properties));
    }

    [Fact]
    public void NoDeviceLocalHeapMeansUnknown() =>
        Assert.Equal(0, VulkanProbe.LargestDeviceLocalHeap(new byte[PropertiesSize]));

    private static void Heap(byte[] properties, int index, long size, bool deviceLocal)
    {
        var heap = properties.AsSpan(HeapsOffset + (index * HeapStride));
        BinaryPrimitives.WriteUInt64LittleEndian(heap, (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(heap[HeapFlagsOffset..], deviceLocal ? 1u : 0u);
    }
}
