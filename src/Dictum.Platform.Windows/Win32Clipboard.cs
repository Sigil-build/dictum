using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dictum.Platform.Injection;

namespace Dictum.Platform.Windows;

/// <summary>
/// CF_UNICODETEXT clipboard access. A message-only window owns the clipboard while writing:
/// with a null owner, EmptyClipboard leaves no owner and SetClipboardData fails.
/// </summary>
/// <param name="openTimeout">How long to wait for another app to close the clipboard; 2 s when not set.</param>
[SupportedOSPlatform("windows")]
public sealed unsafe class Win32Clipboard(TimeSpan? openTimeout = null) : IClipboard
{
    private static readonly TimeSpan FirstPause = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LongestPause = TimeSpan.FromMilliseconds(200);

    private readonly TimeSpan _openTimeout = openTimeout ?? TimeSpan.FromSeconds(2);

    public string Backend => "Win32 clipboard";

    public Task<string?> GetTextAsync(CancellationToken ct) =>
        Task.FromResult(WithClipboard(nint.Zero, ct, () =>
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
            WithClipboard(owner, ct, () =>
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

    /// <summary>
    /// The clipboard history, clipboard managers and apps that hand over their data late keep the clipboard open
    /// for a moment, sometimes for hundreds of milliseconds, so opening is retried with growing pauses. The wait
    /// blocks the thread: the clipboard is opened, used and closed on one thread.
    /// </summary>
    private string? WithClipboard(nint owner, CancellationToken ct, Func<string?> action)
    {
        var waited = Stopwatch.StartNew();
        var pause = FirstPause;
        while (!Win32.OpenClipboard(owner))
        {
            var error = Marshal.GetLastPInvokeError();
            if (waited.Elapsed >= _openTimeout)
            {
                throw new Win32Exception(error, $"The clipboard is in use by another application (waited {_openTimeout.TotalSeconds:0.#} s)");
            }

            ct.WaitHandle.WaitOne(pause);
            ct.ThrowIfCancellationRequested();
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));
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
