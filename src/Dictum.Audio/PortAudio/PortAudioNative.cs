using System.Runtime.InteropServices;

namespace Dictum.Audio.PortAudio;

/// <summary>Minimal PortAudio v19 bindings for blocking capture and playback.</summary>
internal static unsafe partial class PortAudioNative
{
    public const string Library = "portaudio";

    public const int NoError = 0;
    public const int InputOverflowed = -9981;
    public const ulong Float32 = 0x00000001;

    public const int HostApiDirectSound = 1;
    public const int HostApiMme = 2;
    public const int HostApiCoreAudio = 5;
    public const int HostApiAlsa = 8;
    public const int HostApiWasapi = 13;

    [LibraryImport(Library)]
    public static partial int Pa_Initialize();

    [LibraryImport(Library)]
    public static partial int Pa_Terminate();

    [LibraryImport(Library)]
    public static partial nint Pa_GetErrorText(int errorCode);

    [LibraryImport(Library)]
    public static partial nint Pa_GetVersionText();

    [LibraryImport(Library)]
    public static partial int Pa_GetDeviceCount();

    [LibraryImport(Library)]
    public static partial int Pa_GetDefaultInputDevice();

    [LibraryImport(Library)]
    public static partial int Pa_GetDefaultOutputDevice();

    [LibraryImport(Library)]
    public static partial int Pa_GetDefaultHostApi();

    [LibraryImport(Library)]
    public static partial int Pa_HostApiTypeIdToHostApiIndex(int type);

    [LibraryImport(Library)]
    public static partial DeviceInfo* Pa_GetDeviceInfo(int device);

    [LibraryImport(Library)]
    public static partial HostApiInfo* Pa_GetHostApiInfo(int hostApi);

    [LibraryImport(Library)]
    public static partial int Pa_IsFormatSupported(StreamParameters* inputParameters, StreamParameters* outputParameters, double sampleRate);

    [LibraryImport(Library)]
    public static partial int Pa_OpenStream(
        nint* stream,
        StreamParameters* inputParameters,
        StreamParameters* outputParameters,
        double sampleRate,
        CULong framesPerBuffer,
        CULong streamFlags,
        nint streamCallback,
        nint userData);

    [LibraryImport(Library)]
    public static partial int Pa_StartStream(nint stream);

    [LibraryImport(Library)]
    public static partial int Pa_StopStream(nint stream);

    [LibraryImport(Library)]
    public static partial int Pa_CloseStream(nint stream);

    [LibraryImport(Library)]
    public static partial int Pa_ReadStream(nint stream, float* buffer, CULong frames);

    [LibraryImport(Library)]
    public static partial int Pa_WriteStream(nint stream, float* buffer, CULong frames);

    public static string ErrorText(int error) =>
        Marshal.PtrToStringUTF8(Pa_GetErrorText(error)) ?? $"PortAudio error {error}";

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int StructVersion;
        public byte* Name;
        public int HostApi;
        public int MaxInputChannels;
        public int MaxOutputChannels;
        public double DefaultLowInputLatency;
        public double DefaultLowOutputLatency;
        public double DefaultHighInputLatency;
        public double DefaultHighOutputLatency;
        public double DefaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApiInfo
    {
        public int StructVersion;
        public int Type;
        public byte* Name;
        public int DeviceCount;
        public int DefaultInputDevice;
        public int DefaultOutputDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamParameters
    {
        public int Device;
        public int ChannelCount;

        // PaSampleFormat is 'unsigned long': 32-bit on Windows, 64-bit on Linux and macOS.
        public CULong SampleFormat;
        public double SuggestedLatency;
        public void* HostApiSpecificStreamInfo;
    }
}
