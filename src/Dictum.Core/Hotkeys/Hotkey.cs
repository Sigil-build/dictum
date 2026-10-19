namespace Dictum.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,

    /// <summary>Windows / Super / Command key.</summary>
    Meta = 8,
}

/// <summary>The non-modifier key of a hotkey. Left and right modifiers are not distinguished.</summary>
public enum HotkeyKey
{
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    Space, Tab, Enter, Escape, Backspace,
    Grave, Minus, Equals, OpenBracket, CloseBracket, Backslash, Semicolon, Quote, Comma, Period, Slash,
    Insert, Delete, Home, End, PageUp, PageDown, Up, Down, Left, Right,
    Pause, ScrollLock, PrintScreen,
}

/// <summary>A key combination such as <c>Ctrl+Alt+Space</c>.</summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, HotkeyKey Key)
{
    /// <summary>Joins the parts of a hotkey's text, as in <c>Ctrl+Alt+Space</c>; so it cannot itself be a key.</summary>
    public const char Separator = '+';

    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifiers.Ctrl,
        ["control"] = HotkeyModifiers.Ctrl,
        ["alt"] = HotkeyModifiers.Alt,
        ["option"] = HotkeyModifiers.Alt,
        ["shift"] = HotkeyModifiers.Shift,
        ["meta"] = HotkeyModifiers.Meta,
        ["super"] = HotkeyModifiers.Meta,
        ["win"] = HotkeyModifiers.Meta,
        ["cmd"] = HotkeyModifiers.Meta,
        ["logo"] = HotkeyModifiers.Meta,
    };

    private static readonly Dictionary<string, HotkeyKey> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["`"] = HotkeyKey.Grave,
        ["~"] = HotkeyKey.Grave,
        ["tilde"] = HotkeyKey.Grave,
        ["backtick"] = HotkeyKey.Grave,
        ["return"] = HotkeyKey.Enter,
        ["esc"] = HotkeyKey.Escape,
        ["del"] = HotkeyKey.Delete,
        ["ins"] = HotkeyKey.Insert,
        ["-"] = HotkeyKey.Minus,
        ["="] = HotkeyKey.Equals,
        ["["] = HotkeyKey.OpenBracket,
        ["]"] = HotkeyKey.CloseBracket,
        ["\\"] = HotkeyKey.Backslash,
        [";"] = HotkeyKey.Semicolon,
        ["'"] = HotkeyKey.Quote,
        [","] = HotkeyKey.Comma,
        ["."] = HotkeyKey.Period,
        ["/"] = HotkeyKey.Slash,
        ["pgup"] = HotkeyKey.PageUp,
        ["pgdn"] = HotkeyKey.PageDown,
    };

    public static Hotkey Parse(string text) =>
        TryParse(text, out var hotkey, out var error) ? hotkey : throw new FormatException(error);

    /// <summary>Parses "Ctrl+Alt+Space", "super+`", "F13"; exactly one non-modifier key is required.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey, out string error)
    {
        hotkey = new Hotkey(HotkeyModifiers.None, HotkeyKey.Space);
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "hotkey is empty";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        HotkeyKey? key = null;
        foreach (var raw in SplitParts(text))
        {
            var part = raw.Trim();
            if (ModifierNames.TryGetValue(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                error = $"'{text}' has more than one non-modifier key";
                return false;
            }

            if (!TryParseKey(part, out var parsed))
            {
                error = $"'{part}' is not a known key";
                return false;
            }

            key = parsed;
        }

        if (key is null)
        {
            error = $"'{text}' has no key besides modifiers";
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value);
        error = string.Empty;
        return true;
    }

    /// <summary>Canonical form, e.g. "Ctrl+Alt+Space".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(Key switch
        {
            >= HotkeyKey.D0 and <= HotkeyKey.D9 => ((char)('0' + (Key - HotkeyKey.D0))).ToString(),
            HotkeyKey.Grave => "`",
            _ => Key.ToString(),
        });
        return string.Join(Separator, parts);
    }

    private static bool TryParseKey(string part, out HotkeyKey key)
    {
        if (KeyAliases.TryGetValue(part, out key))
        {
            return true;
        }

        if (part.Length == 1 && char.IsAsciiDigit(part[0]))
        {
            key = HotkeyKey.D0 + (part[0] - '0');
            return true;
        }

        return Enum.TryParse(part, ignoreCase: true, out key) && !int.TryParse(part, out _);
    }

    private static string[] SplitParts(string text) =>
        text.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
