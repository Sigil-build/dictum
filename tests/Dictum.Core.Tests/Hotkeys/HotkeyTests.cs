using Dictum.Core.Configuration;
using Dictum.Core.Hotkeys;
using Dictum.Platform.Input;

namespace Dictum.Core.Tests.Hotkeys;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.Space)]
    [InlineData("control + alt + space", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.Space)]
    [InlineData("Ctrl+Alt+`", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.Grave)]
    [InlineData("ctrl+alt+tilde", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, HotkeyKey.Grave)]
    [InlineData("Super+Shift+D", HotkeyModifiers.Meta | HotkeyModifiers.Shift, HotkeyKey.D)]
    [InlineData("Win+1", HotkeyModifiers.Meta, HotkeyKey.D1)]
    [InlineData("F13", HotkeyModifiers.None, HotkeyKey.F13)]
    [InlineData("Ctrl+Esc", HotkeyModifiers.Ctrl, HotkeyKey.Escape)]
    public void ParsesCommonSpellings(string text, HotkeyModifiers modifiers, HotkeyKey key) =>
        Assert.Equal(new Hotkey(modifiers, key), Hotkey.Parse(text));

    [Theory]
    [InlineData("alt+ctrl+space", "Ctrl+Alt+Space")]
    [InlineData("meta+shift+ctrl+alt+a", "Ctrl+Alt+Shift+Meta+A")]
    [InlineData("ctrl+alt+~", "Ctrl+Alt+`")]
    [InlineData("ctrl+5", "Ctrl+5")]
    public void FormatsCanonically(string text, string canonical) =>
        Assert.Equal(canonical, Hotkey.Parse(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+12")]
    public void RejectsInvalidHotkeys(string text) =>
        Assert.False(Hotkey.TryParse(text, out _, out var error) || error.Length == 0);

    [Fact]
    public void EveryKeyMapsToASharpHookCodeAndBack()
    {
        foreach (var key in Enum.GetValues<HotkeyKey>())
        {
            Assert.Equal(key, SharpHookKeys.FromKeyCode(SharpHookKeys.ToKeyCode(key)));
        }
    }

    [Fact]
    public void ValidatorRejectsBadAndDuplicateHotkeys()
    {
        var config = DefaultConfig.Create() with
        {
            Profiles =
            [
                new ProfileConfig { Id = "a", Stt = "local", Hotkey = "Ctrl+Alt+Space" },
                new ProfileConfig { Id = "b", Stt = "local", Hotkey = "alt+ctrl+space" },
                new ProfileConfig { Id = "c", Stt = "local", Hotkey = "Ctrl+Nope" },
            ],
        };

        var paths = ConfigValidator.Validate(config).Select(i => i.Path).ToList();

        Assert.Equal(["profiles[1].hotkey", "profiles[2].hotkey"], paths);
    }
}
