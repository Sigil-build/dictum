using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;

namespace Dictum.Core.Tests.Injection;

public class ClipboardPasteInjectorTests
{
    private static readonly string[] Terminals = ["konsole", "kitty"];
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeClipboard _clipboard = new() { Text = "previous" };
    private readonly FakeKeystrokes _keys = new();

    public ClipboardPasteInjectorTests() => _clipboard.Events = _keys.Events;

    [Fact]
    public async Task PastesThenRestoresThePreviousClipboard()
    {
        var modifiers = new FakeModifiers(_keys);
        var injector = Create(activeApp: "firefox", modifiers: modifiers);

        await injector.InjectAsync("dictated", Options(), _ct);

        Assert.Equal(["set:dictated", "wait-modifiers", "paste:CtrlV", "set:previous"], Log());
        Assert.Equal("previous", _clipboard.Text);
    }

    [Fact]
    public async Task KnownTerminalGetsCtrlShiftV()
    {
        await Create(activeApp: "Konsole").InjectAsync("ls", Options(), _ct);

        Assert.Contains("paste:CtrlShiftV", Log());
    }

    [Theory]
    [InlineData(PasteShortcut.CtrlShiftV, "paste:CtrlShiftV")]
    [InlineData(PasteShortcut.CtrlV, "paste:CtrlV")]
    public async Task ExplicitShortcutWinsOverTheActiveApp(PasteShortcut shortcut, string expected)
    {
        await Create(activeApp: "kitty").InjectAsync("x", Options(shortcut: shortcut), _ct);

        Assert.Contains(expected, Log());
    }

    [Fact]
    public async Task DoesNotRestoreWhenSomethingElseChangedTheClipboard()
    {
        _keys.OnPaste = () => _clipboard.Text = "copied by the user";

        await Create().InjectAsync("dictated", Options(), _ct);

        Assert.Equal("copied by the user", _clipboard.Text);
    }

    [Fact]
    public async Task NonTextClipboardIsLeftWithTheDictatedText()
    {
        _clipboard.Text = null;

        await Create().InjectAsync("dictated", Options(), _ct);

        Assert.Equal(["set:dictated", "paste:CtrlV"], Log());
    }

    [Fact]
    public async Task ClipboardModeOnlySetsTheClipboard()
    {
        await Create().InjectAsync("dictated", Options(OutputMode.Clipboard), _ct);

        Assert.Equal(["set:dictated"], Log());
    }

    [Fact]
    public async Task WithoutKeystrokesTheTextStaysOnTheClipboardAndTheCallerIsTold()
    {
        var injector = new ClipboardPasteInjector(_clipboard, null, null, null, Terminals, NullLog.Instance);

        await Assert.ThrowsAsync<InjectionUnavailableException>(() => injector.InjectAsync("dictated", Options(), _ct));
        Assert.False(injector.Capabilities.Paste);
    }

    private ClipboardPasteInjector Create(string? activeApp = null, IModifierState? modifiers = null) =>
        new(_clipboard, _keys, new FakeActiveApp(activeApp), modifiers, Terminals, NullLog.Instance);

    private static InjectionOptions Options(OutputMode mode = OutputMode.Paste, PasteShortcut shortcut = PasteShortcut.Auto) =>
        new(mode, shortcut, TimeSpan.Zero);

    private List<string> Log() => _keys.Events;

    private sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; set; }

        public List<string>? Events { get; set; }

        public string Backend => "fake";

        public Task<string?> GetTextAsync(CancellationToken ct) => Task.FromResult(Text);

        public Task SetTextAsync(string text, CancellationToken ct)
        {
            Text = text;
            Events?.Add($"set:{text}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeKeystrokes : IKeystrokeSender
    {
        public List<string> Events { get; } = [];

        public Action? OnPaste { get; set; }

        public string Backend => "fake";

        public Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
        {
            Events.Add($"paste:{keys}");
            OnPaste?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeActiveApp(string? app) : IActiveAppProvider
    {
        public string? GetActiveApp() => app;
    }

    private sealed class FakeModifiers(FakeKeystrokes keys) : IModifierState
    {
        public Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken ct)
        {
            keys.Events.Add("wait-modifiers");
            return Task.CompletedTask;
        }
    }
}
