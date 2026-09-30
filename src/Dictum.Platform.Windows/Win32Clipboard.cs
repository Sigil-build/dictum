using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dictum.Platform.Injection;

namespace Dictum.Platform.Windows;

/// <summary>
/// CF_UNICODETEXT clipboard access. A message-only window owns the clipboard while writing:
/// with a null owner, EmptyClipboard leaves no owner and SetClipboardData fails.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class Win32Clipboard : IClipboard
{
    private const int OpenAttempts = 10;

    public string Backend => "Win32 clipboard";

    public Task<string?> GetTextAsync(CancellationToken ct) =>
        Task.FromResult(WithClipboard(nint.Zero, () =>
        {
            var handle = Win32.GetClipboardData(Win32.CfUnicodeText);
            if (handle == 0)
            {
                return null;
            }

            // The clipboard owns the handle: copy the text out, never free it.
            var text = Win32.GlobalLock(handle);
            try
            {
                return text == 0 ? null : Marshal.PtrToStringUni(text);
            }
            finally
            {
                Win32.GlobalUnlock(handle);
            }
        }));

    public Task SetTextAsync(string text, CancellationToken ct)
    {
        var owner = Win32.CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, Win32.HwndMessage, 0, 0, 0);
        try
        {
            WithClipboard(owner, () =>
            {
                Win32.EmptyClipboard();
                var bytes = (nuint)((text.Length + 1) * sizeof(char));
                var memory = Win32.GlobalAlloc(Win32.GmemMoveable, bytes);
                if (memory == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GlobalAlloc failed");
                }

                var target = (char*)Win32.GlobalLock(memory);
                text.AsSpan().CopyTo(new Span<char>(target, text.Length));
                target[text.Length] = '\0';
                Win32.GlobalUnlock(memory);

                // On success the system owns the memory; free it only on failure.
                if (Win32.SetClipboardData(Win32.CfUnicodeText, memory) == 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    Win32.GlobalFree(memory);
                    throw new Win32Exception(error, "SetClipboardData failed");
                }

                return (string?)null;
            });
        }
        finally
        {
            if (owner != 0)
            {
                Win32.DestroyWindow(owner);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Another app may hold the clipboard open briefly, so opening is retried.</summary>
    private static string? WithClipboard(nint owner, Func<string?> action)
    {
        for (var attempt = 1; !Win32.OpenClipboard(owner); attempt++)
        {
            if (attempt == OpenAttempts)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The clipboard is in use by another application");
            }

            Thread.Sleep(20);
        }

        try
        {
            return action();
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }
}
