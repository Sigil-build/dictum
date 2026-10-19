using Dictum.Platform.Injection;

namespace Dictum.Platform.Linux;

/// <summary>Wayland clipboard through wl-clipboard (wl-copy / wl-paste).</summary>
public sealed class WlClipboard : IClipboard
{
    private bool _sensitiveSupported = true;

    public string Backend => "wl-clipboard";

    public static bool IsAvailable => Tools.Exists("wl-copy") && Tools.Exists("wl-paste");

    public async Task<string?> GetTextAsync(CancellationToken ct)
    {
        var result = await Tools.RunAsync("wl-paste", ["--no-newline", "--type", "text"], null, ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task SetTextAsync(string text, CancellationToken ct)
    {
        // --sensitive asks clipboard managers not to keep dictated text in their history (wl-clipboard 2.3+).
        if (_sensitiveSupported)
        {
            var result = await Tools.RunAsync("wl-copy", ["--sensitive", "--type", "text/plain;charset=utf-8"], text, ct).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return;
            }

            _sensitiveSupported = false;
        }

        var plain = await Tools.RunAsync("wl-copy", ["--type", "text/plain;charset=utf-8"], text, ct).ConfigureAwait(false);
        if (plain.ExitCode != 0)
        {
            throw new InjectionUnavailableException($"wl-copy failed: {plain.Error.Trim()}");
        }
    }
}

/// <summary>X11 clipboard through xclip, or xsel when xclip is missing.</summary>
public sealed class XClipboard : IClipboard
{
    private readonly bool _xclip = Tools.Exists("xclip");

    public string Backend => _xclip ? "xclip" : "xsel";

    public static bool IsAvailable => Tools.Exists("xclip") || Tools.Exists("xsel");

    public async Task<string?> GetTextAsync(CancellationToken ct)
    {
        var result = _xclip
            ? await Tools.RunAsync("xclip", ["-selection", "clipboard", "-o", "-t", "UTF8_STRING"], null, ct).ConfigureAwait(false)
            : await Tools.RunAsync("xsel", ["--clipboard", "--output"], null, ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task SetTextAsync(string text, CancellationToken ct)
    {
        var result = _xclip
            ? await Tools.RunAsync("xclip", ["-selection", "clipboard", "-i"], text, ct).ConfigureAwait(false)
            : await Tools.RunAsync("xsel", ["--clipboard", "--input"], text, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InjectionUnavailableException($"{Backend} failed: {result.Error.Trim()}");
        }
    }
}
