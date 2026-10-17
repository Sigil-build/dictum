using Avalonia.Input;
using Dictum.App.Settings;
using Dictum.Core.Hotkeys;

namespace Dictum.App.Tests.Settings;

public sealed class HotkeyKeysTests
{
    [Theory]
    [InlineData(PhysicalKey.A, HotkeyKey.A)]
    [InlineData(PhysicalKey.Z, HotkeyKey.Z)]
    [InlineData(PhysicalKey.Digit0, HotkeyKey.D0)]
    [InlineData(PhysicalKey.Digit9, HotkeyKey.D9)]
    [InlineData(PhysicalKey.F1, HotkeyKey.F1)]
    [InlineData(PhysicalKey.F24, HotkeyKey.F24)]
    [InlineData(PhysicalKey.Space, HotkeyKey.Space)]
    [InlineData(PhysicalKey.Backquote, HotkeyKey.Grave)]
    [InlineData(PhysicalKey.Equal, HotkeyKey.Equals)]
    [InlineData(PhysicalKey.BracketLeft, HotkeyKey.OpenBracket)]
    [InlineData(PhysicalKey.Quote, HotkeyKey.Quote)]
    [InlineData(PhysicalKey.Slash, HotkeyKey.Slash)]
    [InlineData(PhysicalKey.PageDown, HotkeyKey.PageDown)]
    [InlineData(PhysicalKey.ArrowLeft, HotkeyKey.Left)]
    [InlineData(PhysicalKey.PrintScreen, HotkeyKey.PrintScreen)]
    public void PhysicalKeysMapToHotkeyKeys(PhysicalKey key, HotkeyKey expected) => Assert.Equal(expected, HotkeyKeys.Key(key));

    [Theory]
    [InlineData(PhysicalKey.NumPad5)]
    [InlineData(PhysicalKey.CapsLock)]
    [InlineData(PhysicalKey.ControlLeft)]
    public void OtherKeysDoNotMap(PhysicalKey key) => Assert.Null(HotkeyKeys.Key(key));

    [Theory]
    [InlineData(PhysicalKey.ControlRight)]
    [InlineData(PhysicalKey.ShiftLeft)]
    [InlineData(PhysicalKey.AltRight)]
    [InlineData(PhysicalKey.MetaLeft)]
    public void ModifierKeysAreModifiers(PhysicalKey key) => Assert.True(HotkeyKeys.IsModifier(key));

    [Fact]
    public void AChordIsTheCanonicalHotkey()
    {
        var chord = HotkeyKeys.Chord(PhysicalKey.Space, KeyModifiers.Control | KeyModifiers.Alt);

        Assert.Equal("Ctrl+Alt+Space", chord?.ToString());
        Assert.Equal("Ctrl+Shift+Meta+F13", HotkeyKeys.Chord(PhysicalKey.F13, KeyModifiers.Meta | KeyModifiers.Shift | KeyModifiers.Control)?.ToString());
        Assert.Null(HotkeyKeys.Chord(PhysicalKey.AltLeft, KeyModifiers.Alt));
    }

    [Fact]
    public void ScreenReadersHearTheKeysApart()
    {
        Assert.Equal("Ctrl + Alt + Space", HotkeyKeys.Spoken("Ctrl+Alt+Space"));
        Assert.Equal("", HotkeyKeys.Spoken(null));
    }
}
