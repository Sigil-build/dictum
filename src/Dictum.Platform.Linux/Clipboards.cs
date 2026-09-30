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

/// <summary>WM_CLASS of the focused X11 window via xprop, e.g. "konsole".</summary>
public sealed class XpropActiveApp : IActiveAppProvider
{
    public string? GetActiveApp()
    {
        try
        {
            var root = Tools.RunAsync("xprop", ["-root", "_NET_ACTIVE_WINDOW"], null, CancellationToken.None, 1000).GetAwaiter().GetResult();
            var id = ParseWindowId(root.Output);
            if (id is null)
            {
                return null;
            }

            var window = Tools.RunAsync("xprop", ["-id", id, "WM_CLASS"], null, CancellationToken.None, 1000).GetAwaiter().GetResult();
            return ParseWmClass(window.Output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>"_NET_ACTIVE_WINDOW(WINDOW): window id # 0x3a00007" → "0x3a00007".</summary>
    public static string? ParseWindowId(string output)
    {
        var hash = output.LastIndexOf('#');
        var id = hash < 0 ? "" : output[(hash + 1)..].Trim().Split(',')[0].Trim();
        return id.StartsWith("0x", StringComparison.Ordinal) && id != "0x0" ? id : null;
    }

    /// <summary>"WM_CLASS(STRING) = "konsole", "konsole"" → the class (second) part, lower-cased.</summary>
    public static string? ParseWmClass(string output)
    {
        var parts = output.Split('"');
        return parts.Length >= 4 ? parts[3].ToLowerInvariant() : parts.Length >= 2 ? parts[1].ToLowerInvariant() : null;
    }
}
