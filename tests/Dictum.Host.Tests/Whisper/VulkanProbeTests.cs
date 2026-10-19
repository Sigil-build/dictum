using System.Buffers.Binary;
using Dictum.Stt.Whisper;

namespace Dictum.Host.Tests.Whisper;

public class VulkanProbeTests
{
    [Fact]
    public void TheLargestDeviceLocalHeapIsTheGpuMemory()
    {
        var properties = new byte[520];
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(260), 3);
        Heap(properties, 0, 256L << 20, deviceLocal: true);
        Heap(properties, 1, 32L << 30, deviceLocal: false);
        Heap(properties, 2, 8L << 30, deviceLocal: true);

        Assert.Equal(8L << 30, VulkanProbe.LargestDeviceLocalHeap(properties));
    }

    [Fact]
    public void NoDeviceLocalHeapMeansUnknown() =>
        Assert.Equal(0, VulkanProbe.LargestDeviceLocalHeap(new byte[520]));

    private static void Heap(byte[] properties, int index, long size, bool deviceLocal)
    {
        var heap = properties.AsSpan(264 + (index * 16));
        BinaryPrimitives.WriteUInt64LittleEndian(heap, (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(heap[8..], deviceLocal ? 1u : 0u);
    }
}
