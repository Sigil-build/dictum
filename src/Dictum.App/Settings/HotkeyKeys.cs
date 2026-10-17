using Avalonia.Input;
using Dictum.Core.Hotkeys;

namespace Dictum.App.Settings;

/// <summary>
/// Avalonia's physical keys as Dictum hotkeys (G3). Physical keys keep a chord the same whatever the keyboard layout,
/// as the daemon's hook does. Number-pad keys and keys Dictum cannot bind map to nothing.
/// </summary>
public static class HotkeyKeys
{
    public static HotkeyKey? Key(PhysicalKey key) => key switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => HotkeyKey.A + (key - PhysicalKey.A),
        >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => HotkeyKey.D0 + (key - PhysicalKey.Digit0),
        >= PhysicalKey.F1 and <= PhysicalKey.F24 => HotkeyKey.F1 + (key - PhysicalKey.F1),
        PhysicalKey.Space => HotkeyKey.Space,
        PhysicalKey.Tab => HotkeyKey.Tab,
        PhysicalKey.Enter => HotkeyKey.Enter,
        PhysicalKey.Escape => HotkeyKey.Escape,
        PhysicalKey.Backspace => HotkeyKey.Backspace,
        PhysicalKey.Backquote => HotkeyKey.Grave,
        PhysicalKey.Minus => HotkeyKey.Minus,
        PhysicalKey.Equal => HotkeyKey.Equals,
        PhysicalKey.BracketLeft => HotkeyKey.OpenBracket,
        PhysicalKey.BracketRight => HotkeyKey.CloseBracket,
        PhysicalKey.Backslash => HotkeyKey.Backslash,
        PhysicalKey.Semicolon => HotkeyKey.Semicolon,
        PhysicalKey.Quote => HotkeyKey.Quote,
        PhysicalKey.Comma => HotkeyKey.Comma,
        PhysicalKey.Period => HotkeyKey.Period,
        PhysicalKey.Slash => HotkeyKey.Slash,
        PhysicalKey.Insert => HotkeyKey.Insert,
        PhysicalKey.Delete => HotkeyKey.Delete,
        PhysicalKey.Home => HotkeyKey.Home,
        PhysicalKey.End => HotkeyKey.End,
        PhysicalKey.PageUp => HotkeyKey.PageUp,
        PhysicalKey.PageDown => HotkeyKey.PageDown,
        PhysicalKey.ArrowUp => HotkeyKey.Up,
        PhysicalKey.ArrowDown => HotkeyKey.Down,
        PhysicalKey.ArrowLeft => HotkeyKey.Left,
        PhysicalKey.ArrowRight => HotkeyKey.Right,
        PhysicalKey.Pause => HotkeyKey.Pause,
        PhysicalKey.ScrollLock => HotkeyKey.ScrollLock,
        PhysicalKey.PrintScreen => HotkeyKey.PrintScreen,
        _ => null,
    };

    public static bool IsModifier(PhysicalKey key) => key is PhysicalKey.ControlLeft or PhysicalKey.ControlRight
        or PhysicalKey.ShiftLeft or PhysicalKey.ShiftRight or PhysicalKey.AltLeft or PhysicalKey.AltRight
        or PhysicalKey.MetaLeft or PhysicalKey.MetaRight;

    public static HotkeyModifiers Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? HotkeyModifiers.Ctrl : HotkeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Alt) ? HotkeyModifiers.Alt : HotkeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Shift) ? HotkeyModifiers.Shift : HotkeyModifiers.None)
        | (modifiers.HasFlag(KeyModifiers.Meta) ? HotkeyModifiers.Meta : HotkeyModifiers.None);

    /// <summary>The hotkey a key press makes; null for a modifier alone or a key Dictum cannot bind.</summary>
    public static Hotkey? Chord(PhysicalKey key, KeyModifiers modifiers) =>
        IsModifier(key) || Key(key) is not { } hotkeyKey ? null : new Hotkey(Modifiers(modifiers), hotkeyKey);

    /// <summary>"Ctrl+Alt+Space" as "Ctrl + Alt + Space", so a screen reader says each key.</summary>
    public static string Spoken(string? hotkey) => hotkey is null ? "" : string.Join(" + ", hotkey.Split('+'));
}
