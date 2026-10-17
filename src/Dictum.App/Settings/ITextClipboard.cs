using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Dictum.App.Settings;

/// <summary>Text to the clipboard; the window's clipboard in the app, a fake in tests.</summary>
public interface ITextClipboard
{
    Task SetTextAsync(string text);
}

/// <summary>The clipboard of the Settings window, when one is open.</summary>
public sealed class WindowClipboard(Func<TopLevel?> window) : ITextClipboard
{
    public async Task SetTextAsync(string text)
    {
        if (window()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
