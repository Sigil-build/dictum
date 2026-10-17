using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Resources;

namespace Dictum.App.Tests.Views;

public class HotkeyRecorderTests(AvaloniaFixture fx)
{
    private static (Window Window, HotkeyRecorder Recorder, TextBox Other) Show(string? hotkey = "Ctrl+Alt+Space")
    {
        var recorder = new HotkeyRecorder { Hotkey = hotkey };
        var other = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { recorder, other } }, Width = 500, Height = 200 };
        window.Show();
        recorder.Focus();
        recorder.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return (window, recorder, other);
    }

    [Fact]
    public Task AClickThenAChordRecordsIt() => fx.Ui(() =>
    {
        var (window, recorder, _) = Show(null);
        Assert.True(recorder.IsRecording);

        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        Assert.True(recorder.IsRecording);
        window.KeyPress(Key.H, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.H, "h");

        Assert.False(recorder.IsRecording);
        Assert.Equal("Ctrl+Alt+H", recorder.Hotkey);
        window.Close();
    });

    [Fact]
    public Task WhileListeningTheButtonSaysHowToStop() => fx.Ui(() =>
    {
        var (window, recorder, _) = Show();

        var texts = recorder.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains(Strings.SettingsHotkeyListening, texts);
        window.Close();
    });

    [Fact]
    public Task EscapeCancelsAndKeepsTheHotkey() => fx.Ui(() =>
    {
        var (window, recorder, _) = Show();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.False(recorder.IsRecording);
        Assert.Equal("Ctrl+Alt+Space", recorder.Hotkey);
        window.Close();
    });

    [Fact]
    public Task BackspaceRemovesTheHotkey() => fx.Ui(() =>
    {
        var (window, recorder, _) = Show();

        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);

        Assert.False(recorder.IsRecording);
        Assert.Null(recorder.Hotkey);
        window.Close();
    });

    // Review focus 1.
    [Fact]
    public Task LosingFocusCancelsRecording() => fx.Ui(() =>
    {
        var (window, recorder, other) = Show();

        other.Focus();

        Assert.False(recorder.IsRecording);
        window.Close();
    });

    [Fact]
    public Task TheKeysShowAsKeycapsAndAreSpokenApart() => fx.Ui(() =>
    {
        var (window, recorder, _) = Show();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        var caps = recorder.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("keycap"))
            .Select(b => ((TextBlock)b.Child!).Text).ToList();

        Assert.Equal(["Ctrl", "Alt", "Space"], caps);
        Assert.Equal("Hotkey: Ctrl + Alt + Space. Press to change", AutomationProperties.GetName(recorder));
        window.Close();
    });
}
