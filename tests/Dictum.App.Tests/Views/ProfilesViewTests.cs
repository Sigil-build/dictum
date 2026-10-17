using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class ProfilesViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (Window Window, SettingsSession Session) Show()
    {
        Directory.CreateDirectory(_dir);
        var config = ProfilesPageTests.Sample();
        config.Profiles[0].Pipeline = ["cleanup", "terms", "filler"];
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(config);
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new ProfilesPage(session, new FakeChannel(), NullLog.Instance);
        var window = new Window { Content = new ViewLocator().Build(page), Width = 960, Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, session);
    }

    private static List<string> Pipeline(SettingsSession session) => session.Config.Profiles[0].Pipeline;

    [Fact]
    public Task TheListTheRecorderAndTheChipsShow() => fx.Ui(() =>
    {
        var (window, _) = Show();

        Assert.IsType<ProfilesView>(window.Content);
        Assert.Single(window.GetVisualDescendants().OfType<HotkeyRecorder>());
        var chips = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(4, chips.Count);
        Assert.Equal(3, window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("chainRow")));
        window.Close();
    });

    [Fact]
    public Task DraggingAStepByItsHandleMovesIt() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var handle = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("handle"));
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 40));
        window.MouseMove(start + new Point(0, 80));
        window.MouseUp(start + new Point(0, 80), MouseButton.Left);

        Assert.Equal(["terms", "filler", "cleanup"], Pipeline(session));
        window.Close();
    });

    [Fact]
    public Task AltDownMovesTheFocusedStepAndKeepsFocusOnIt() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var first = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("chainRow"));
        first.Focus();

        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["terms", "cleanup", "filler"], Pipeline(session));
        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Border;
        Assert.Equal("cleanup", (focused?.DataContext as ChainStep)?.Id);
        window.Close();
    });


    private static TextBox WordBox(Window window) => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WordBox");

    private static void Type(Window window, TextBox box, string text)
    {
        box.Focus();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    // Final review C1: the real box must clear, and a removed chip must not come back.
    [Fact]
    public Task TypingACommaAddsTheWordAndClearsTheBox() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var box = WordBox(window);

        Type(window, box, "KDE,");
        Assert.Contains("KDE", session.Config.Profiles[0].Vocabulary);
        Assert.Equal("", box.Text);

        var page = (ProfilesPage)((ProfilesView)window.Content!).DataContext!;
        page.Words.Single(w => w.Text == "KDE").Remove.Execute(null);
        Type(window, box, "W");

        Assert.DoesNotContain("KDE", session.Config.Profiles[0].Vocabulary);
        Assert.Equal("W", box.Text);
        window.Close();
    });

    // Final review C1: a half-typed word must not move to the next profile.
    [Fact]
    public Task SwitchingProfilesClearsTheWordBox() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var box = WordBox(window);
        Type(window, box, "Kub");

        window.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemCount == 2).SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", box.Text);

        Type(window, box, "x,");
        Assert.Equal(["x"], session.Config.Profiles[1].Vocabulary);
        Assert.Equal(["Nexus5", "Avalonia"], session.Config.Profiles[0].Vocabulary);
        window.Close();
    });

    // Final review C1: a picker that stays on its last pick cannot pick the same language again.
    [Fact]
    public Task PickingALanguageClearsThePicker() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LanguagePicker");

        picker.SelectedItem = picker.Items.OfType<Option<string>>().Single(o => o.Value == "pl");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("uk, en, pl", session.Config.Profiles[0].Language);
        Assert.Null(picker.SelectedItem);
        window.Close();
    });
}
