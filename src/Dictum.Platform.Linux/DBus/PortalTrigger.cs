using Dictum.Core.Hotkeys;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Formats a hotkey as a shortcuts-spec trigger ("CTRL+ALT+space"): modifiers CTRL, ALT, SHIFT, LOGO,
/// then an xkb keysym name.
/// </summary>
public static class PortalTrigger
{
    private const int MaxParts = 5; // four modifiers and the key

    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(MaxParts);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("CTRL");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("ALT");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("SHIFT");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            parts.Add("LOGO");
        }

        parts.Add(Keysym(hotkey.Key));
        return string.Join('+', parts);
    }

    private static string Keysym(HotkeyKey key) => key switch
    {
        >= HotkeyKey.A and <= HotkeyKey.Z => key.ToString().ToLowerInvariant(),
        >= HotkeyKey.D0 and <= HotkeyKey.D9 => ((char)('0' + (key - HotkeyKey.D0))).ToString(),
        >= HotkeyKey.F1 and <= HotkeyKey.F24 => key.ToString(),
        HotkeyKey.Space => "space",
        HotkeyKey.Enter => "Return",
        HotkeyKey.Backspace => "BackSpace",
        HotkeyKey.Grave => "grave",
        HotkeyKey.Minus => "minus",
        HotkeyKey.Equals => "equal",
        HotkeyKey.OpenBracket => "bracketleft",
        HotkeyKey.CloseBracket => "bracketright",
        HotkeyKey.Backslash => "backslash",
        HotkeyKey.Semicolon => "semicolon",
        HotkeyKey.Quote => "apostrophe",
        HotkeyKey.Comma => "comma",
        HotkeyKey.Period => "period",
        HotkeyKey.Slash => "slash",
        HotkeyKey.PageUp => "Page_Up",
        HotkeyKey.PageDown => "Page_Down",
        HotkeyKey.ScrollLock => "Scroll_Lock",
        HotkeyKey.PrintScreen => "Print",
        _ => key.ToString(),
    };
}
