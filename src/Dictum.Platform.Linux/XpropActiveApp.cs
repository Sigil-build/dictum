using Dictum.Platform.Injection;

namespace Dictum.Platform.Linux;

/// <summary>WM_CLASS of the focused X11 window via xprop, e.g. "konsole".</summary>
public sealed class XpropActiveApp : IActiveAppProvider
{
    internal const string Program = "xprop";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    public static bool IsAvailable => Tools.Exists(Program);

    public async ValueTask<string?> GetActiveAppAsync(CancellationToken ct)
    {
        try
        {
            var root = await Tools.RunAsync(Program, ["-root", "_NET_ACTIVE_WINDOW"], null, ct, QueryTimeout).ConfigureAwait(false);
            var id = ParseWindowId(root.Output);
            if (id is null)
            {
                return null;
            }

            var window = await Tools.RunAsync(Program, ["-id", id, "WM_CLASS"], null, ct, QueryTimeout).ConfigureAwait(false);
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
