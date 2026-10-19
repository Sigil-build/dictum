using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Dictum.Stt.Whisper;

public enum VulkanDeviceType
{
    Other = 0,
    IntegratedGpu = 1,
    DiscreteGpu = 2,
    VirtualGpu = 3,
    Cpu = 4,
}

public sealed record VulkanDevice(string Name, VulkanDeviceType Type, long MemoryBytes = 0)
{
    /// <summary>Real graphics hardware; software rasterisers (llvmpipe, WARP via Dozen) report Cpu.</summary>
    public bool IsHardware => Type is VulkanDeviceType.DiscreteGpu or VulkanDeviceType.IntegratedGpu;

    public override string ToString() => $"{Name} ({Type})";
}

/// <summary>
/// Lists Vulkan devices before Whisper picks a runtime. Windows 11 can expose Vulkan on top of the
/// CPU-only WARP renderer (the "OpenCL, OpenGL and Vulkan Compatibility Pack"), which Whisper would
/// happily use: far slower than the CPU runtime and prone to wrong results. Only hardware GPUs count.
/// </summary>
public static unsafe partial class VulkanProbe
{
    private const string Library = "vulkan";
    private const int StructureTypeInstanceCreateInfo = 1;

    // VkPhysicalDeviceProperties is about 824 bytes; only deviceType and deviceName are read.
    private const int DevicePropertiesBufferSize = 1024;
    private const int DeviceTypeOffset = 16;
    private const int DeviceNameOffset = 20;

    // VkPhysicalDeviceMemoryProperties: a type count, 32 memory types of 8 bytes, a heap count, then
    // VK_MAX_MEMORY_HEAPS heaps of 16 bytes (size, then flags).
    private const int MemoryPropertiesSize = 520;
    private const int HeapCountOffset = 260;
    private const int HeapsOffset = 264;
    private const int HeapStride = 16;
    private const int HeapFlagsOffset = 8;
    private const uint MaxMemoryHeaps = 16;
    private const uint HeapDeviceLocalBit = 1;

    private static readonly Lazy<IReadOnlyList<VulkanDevice>> Cached = new(Enumerate);
    private static int _resolverInstalled;

    /// <summary>Vulkan devices on this machine; empty when there is no Vulkan loader or no device.</summary>
    public static IReadOnlyList<VulkanDevice> Devices => Cached.Value;

    public static bool HasHardwareGpu => Devices.Any(d => d.IsHardware);

    private static IReadOnlyList<VulkanDevice> Enumerate()
    {
        try
        {
            if (Interlocked.Exchange(ref _resolverInstalled, 1) == 0)
            {
                NativeLibrary.SetDllImportResolver(typeof(VulkanProbe).Assembly, Resolve);
            }

            var info = new InstanceCreateInfo { SType = StructureTypeInstanceCreateInfo };
            nint instance;
            if (vkCreateInstance(&info, 0, &instance) != 0)
            {
                return [];
            }

            try
            {
                uint count = 0;
                if (vkEnumeratePhysicalDevices(instance, &count, null) != 0 || count == 0)
                {
                    return [];
                }

                var handles = stackalloc nint[(int)count];
                vkEnumeratePhysicalDevices(instance, &count, handles);
                var devices = new List<VulkanDevice>((int)count);

                var properties = stackalloc byte[DevicePropertiesBufferSize];
                var memory = stackalloc byte[MemoryPropertiesSize];
                for (var i = 0; i < count; i++)
                {
                    vkGetPhysicalDeviceProperties(handles[i], properties);
                    var type = (VulkanDeviceType)(*(uint*)(properties + DeviceTypeOffset));
                    var name = Marshal.PtrToStringUTF8((nint)(properties + DeviceNameOffset)) ?? "unknown";
                    vkGetPhysicalDeviceMemoryProperties(handles[i], memory);
                    devices.Add(new VulkanDevice(name, type, LargestDeviceLocalHeap(new ReadOnlySpan<byte>(memory, MemoryPropertiesSize))));
                }

                return [.. devices.Distinct()];
            }
            finally
            {
                vkDestroyInstance(instance, 0);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The size of the largest DEVICE_LOCAL heap in a <c>VkPhysicalDeviceMemoryProperties</c>; 0 when there is none.</summary>
    public static long LargestDeviceLocalHeap(ReadOnlySpan<byte> memoryProperties)
    {
        var count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(memoryProperties[HeapCountOffset..]), MaxMemoryHeaps);
        long largest = 0;
        for (var i = 0; i < count; i++)
        {
            var heap = memoryProperties[(HeapsOffset + (i * HeapStride))..];
            if ((BinaryPrimitives.ReadUInt32LittleEndian(heap[HeapFlagsOffset..]) & HeapDeviceLocalBit) != 0)
            {
                largest = Math.Max(largest, (long)BinaryPrimitives.ReadUInt64LittleEndian(heap));
            }
        }

        return largest;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
        {
            return 0;
        }

        var candidate = OperatingSystem.IsWindows() ? "vulkan-1.dll" : OperatingSystem.IsMacOS() ? "libvulkan.1.dylib" : "libvulkan.so.1";
        return NativeLibrary.TryLoad(candidate, out var handle) ? handle : 0;
    }

    [LibraryImport(Library)]
    private static partial int vkCreateInstance(InstanceCreateInfo* createInfo, nint allocator, nint* instance);

    [LibraryImport(Library)]
    private static partial void vkDestroyInstance(nint instance, nint allocator);

    [LibraryImport(Library)]
    private static partial int vkEnumeratePhysicalDevices(nint instance, uint* count, nint* devices);

    [LibraryImport(Library)]
    private static partial void vkGetPhysicalDeviceProperties(nint device, byte* properties);

    [LibraryImport(Library)]
    private static partial void vkGetPhysicalDeviceMemoryProperties(nint device, byte* properties);

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public int SType;
        public nint Next;
        public uint Flags;
        public nint ApplicationInfo;
        public uint EnabledLayerCount;
        public nint EnabledLayerNames;
        public uint EnabledExtensionCount;
        public nint EnabledExtensionNames;
    }
}
