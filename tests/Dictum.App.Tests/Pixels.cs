using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Dictum.App.Tests;

/// <summary>A bitmap's pixels as BGRA, premultiplied (what a RenderTargetBitmap holds, research §6).</summary>
internal static class Pixels
{
    public static byte[] Of(Bitmap bitmap)
    {
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var buffer = new byte[width * height * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), buffer.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    public static (byte B, byte G, byte R, byte A) At(byte[] pixels, int size, int x, int y)
    {
        var i = (y * size + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    /// <summary>Opaque pixels close to the tray red #FF5A4E.</summary>
    public static int Red(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 255 && pixels[i + 2] > 220 && pixels[i + 1] < 130 && pixels[i] < 120)
            {
                count++;
            }
        }

        return count;
    }
}
