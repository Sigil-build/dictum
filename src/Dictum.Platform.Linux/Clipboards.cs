using Dictum.Platform.Injection;

namespace Dictum.Platform.Linux;

/// <summary>Wayland clipboard through wl-clipboard (wl-copy / wl-paste).</summary>
public sealed class WlClipboard : IClipboard
{
    internal const string Package = "wl-clipboard";

    private const string CopyProgram = "wl-copy";
    private const string PasteProgram = "wl-paste";
    private const string Utf8Text = "text/plain;charset=utf-8";

    private bool _sensitiveSupported = true;

    public string Backend => Package;

    public static bool IsAvailable => Tools.Exists(CopyProgram) && Tools.Exists(PasteProgram);

    public async Task<string?> GetTextAsync(CancellationToken ct)
    {
        var result = await Tools.RunAsync(PasteProgram, ["--no-newline", "--type", "text"], null, ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task SetTextAsync(string text, CancellationToken ct)
    {
        // --sensitive asks clipboard managers not to keep dictated text in their history (wl-clipboard 2.3+).
        if (_sensitiveSupported)
        {
            var result = await Tools.RunAsync(CopyProgram, ["--sensitive", "--type", Utf8Text], text, ct).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return;
            }

            _sensitiveSupported = false;
        }

        var plain = await Tools.RunAsync(CopyProgram, ["--type", Utf8Text], text, ct).ConfigureAwait(false);
        if (plain.ExitCode != 0)
        {
            throw new InjectionUnavailableException($"wl-copy failed: {plain.Error.Trim()}");
        }
    }
}

/// <summary>X11 clipboard through xclip, or xsel when xclip is missing.</summary>
public sealed class XClipboard : IClipboard
{
    internal const string Xclip = "xclip";
    private const string Xsel = "xsel";

    private readonly bool _xclip = Tools.Exists(Xclip);

    public string Backend => _xclip ? Xclip : Xsel;

    public static bool IsAvailable => Tools.Exists(Xclip) || Tools.Exists(Xsel);

    public async Task<string?> GetTextAsync(CancellationToken ct)
    {
        var result = _xclip
            ? await Tools.RunAsync(Xclip, ["-selection", "clipboard", "-o", "-t", "UTF8_STRING"], null, ct).ConfigureAwait(false)
            : await Tools.RunAsync(Xsel, ["--clipboard", "--output"], null, ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task SetTextAsync(string text, CancellationToken ct)
    {
        var result = _xclip
            ? await Tools.RunAsync(Xclip, ["-selection", "clipboard", "-i"], text, ct).ConfigureAwait(false)
            : await Tools.RunAsync(Xsel, ["--clipboard", "--input"], text, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InjectionUnavailableException($"{Backend} failed: {result.Error.Trim()}");
        }
    }
}
