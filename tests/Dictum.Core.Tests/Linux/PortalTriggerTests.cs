using Dictum.Core.Hotkeys;
using Dictum.Platform.Linux.DBus;

namespace Dictum.Core.Tests.Linux;

public class PortalTriggerTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", "CTRL+ALT+space")]
    [InlineData("Ctrl+Alt+`", "CTRL+ALT+grave")]
    [InlineData("Super+Shift+D", "SHIFT+LOGO+d")]
    [InlineData("Ctrl+F9", "CTRL+F9")]
    [InlineData("Alt+5", "ALT+5")]
    [InlineData("Ctrl+Enter", "CTRL+Return")]
    [InlineData("Ctrl+PageDown", "CTRL+Page_Down")]
    public void FormatsShortcutsSpecTriggers(string hotkey, string trigger) =>
        Assert.Equal(trigger, PortalTrigger.Format(Hotkey.Parse(hotkey)));

    [Fact]
    public void EveryKeyHasAKeysym() =>
        Assert.All(Enum.GetValues<HotkeyKey>(), key => Assert.False(string.IsNullOrEmpty(PortalTrigger.Format(new Hotkey(HotkeyModifiers.Ctrl, key)))));
}
