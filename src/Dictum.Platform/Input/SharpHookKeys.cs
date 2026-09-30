using Dictum.Core.Hotkeys;
using SharpHook.Data;

namespace Dictum.Platform.Input;

/// <summary>Maps <see cref="HotkeyKey"/> to SharpHook key codes and classifies modifiers.</summary>
public static class SharpHookKeys
{
    private static readonly Dictionary<HotkeyKey, KeyCode> ToCode = Enum.GetValues<HotkeyKey>().ToDictionary(k => k, Map);
    private static readonly Dictionary<KeyCode, HotkeyKey> FromCode = ToCode.ToDictionary(p => p.Value, p => p.Key);

    public static KeyCode ToKeyCode(HotkeyKey key) => ToCode[key];

    public static HotkeyKey? FromKeyCode(KeyCode code) => FromCode.TryGetValue(code, out var key) ? key : null;

    public static HotkeyModifiers ModifierOf(KeyCode code) => code switch
    {
        KeyCode.VcLeftControl or KeyCode.VcRightControl => HotkeyModifiers.Ctrl,
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt => HotkeyModifiers.Alt,
        KeyCode.VcLeftShift or KeyCode.VcRightShift => HotkeyModifiers.Shift,
        KeyCode.VcLeftMeta or KeyCode.VcRightMeta => HotkeyModifiers.Meta,
        _ => HotkeyModifiers.None,
    };

    private static KeyCode Map(HotkeyKey key) => key switch
    {
        >= HotkeyKey.D0 and <= HotkeyKey.D9 => Enum.Parse<KeyCode>($"Vc{key - HotkeyKey.D0}"),
        HotkeyKey.Grave => KeyCode.VcBackQuote,
        _ => Enum.Parse<KeyCode>($"Vc{key}"),
    };
}
