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

public sealed record VulkanDevice(string Name, VulkanDeviceType Type)
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

                // VkPhysicalDeviceProperties is ~824 bytes; only deviceType (offset 16) and deviceName (offset 20) are read.
                var properties = stackalloc byte[1024];
                for (var i = 0; i < count; i++)
                {
                    vkGetPhysicalDeviceProperties(handles[i], properties);
                    var type = (VulkanDeviceType)(*(uint*)(properties + 16));
                    var name = Marshal.PtrToStringUTF8((nint)(properties + 20)) ?? "unknown";
                    devices.Add(new VulkanDevice(name, type));
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
