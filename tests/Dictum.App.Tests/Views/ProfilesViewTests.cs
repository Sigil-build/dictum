using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Controls;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class ProfilesViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private (Window Window, SettingsSession Session) Show(double width = 960)
    {
        var config = ProfilesPageTests.Sample();
        config.Profiles[0].Pipeline = ["cleanup", "terms", "filler"];
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(config);
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new ProfilesPage(session, new FakeChannel());
        var window = new Window { Content = new ViewLocator().Build(page), Width = width, Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, session);
    }

    private static List<string> Pipeline(SettingsSession session) => session.Config.Profiles[0].Pipeline;

    private static List<Border> LanguageChips(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip") && b.DataContext is LanguageChip)];

    private static string Code(Border chip) => ((LanguageChip)chip.DataContext!).Code;

    private static TextBlock MainMark(Border chip) =>
        chip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.SettingsProfilesLanguageMain);

    private static Button MakeMain(Border chip) =>
        chip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.SettingsProfilesLanguageMakeMain));

    private static List<string?> VisibleTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    // The similar-languages warning starts where the fields start: beside their labels in a wide window, and at the left
    // edge once the rows put each field under its label.
    [Theory]
    [InlineData(960, true)]
    [InlineData(640, false)]
    public Task TheSimilarLanguagesWarningLinesUpWithTheFields(double width, bool beside) => fx.Ui(() =>
    {
        var (window, _) = Show(width);
        var page = (ProfilesPage)((Control)window.Content!).DataContext!;
        page.Languages.ToAdd = page.Languages.All.Single(l => l.Value == "be");
        Dispatcher.UIThread.RunJobs();

        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LanguagePicker");
        var row = picker.GetVisualAncestors().OfType<FieldRow>().First();
        var warning = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == page.Languages.SimilarWarning);
        double Left(Visual visual) => visual.TranslatePoint(default, window)!.Value.X;

        Assert.Equal(beside, row.IsBeside(row.Bounds.Width));
        Assert.Equal(Left(picker), Left(warning), precision: 0);
        window.Close();
    });
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

    // The real box must clear, and a removed chip must not come back.
    [Fact]
    public Task TypingACommaAddsTheWordAndClearsTheBox() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var box = WordBox(window);

        Type(window, box, "KDE,");
        Assert.Contains("KDE", session.Config.Profiles[0].Vocabulary);
        Assert.Equal("", box.Text);

        var page = (ProfilesPage)((ProfilesView)window.Content!).DataContext!;
        page.Words.Chips.Single(w => w.Text == "KDE").Remove.Execute(null);
        Type(window, box, "W");

        Assert.DoesNotContain("KDE", session.Config.Profiles[0].Vocabulary);
        Assert.Equal("W", box.Text);
        window.Close();
    });

    // A half-typed word must not move to the next profile.
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

    // The first chip says it is the main language, and the hint says what that means. Another chip's "Make main" moves it
    // to the front; its button goes with the old chips, so focus moves on to the language picker, where Tab would go.
    [Fact]
    public Task TheMainLanguageIsMarkedAndAnotherChipCanTakeItsPlace() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var chips = LanguageChips(window);
        Assert.Equal(["uk", "en"], chips.Select(Code));
        Assert.Equal((true, false), (MainMark(chips[0]).IsEffectivelyVisible, MakeMain(chips[0]).IsEffectivelyVisible));
        Assert.Equal((false, true), (MainMark(chips[1]).IsEffectivelyVisible, MakeMain(chips[1]).IsEffectivelyVisible));
        Assert.Contains(Strings.SettingsProfilesLanguagesMainHint, VisibleTexts(window));

        var english = chips[1];
        var name = english.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "English");
        var makeMain = MakeMain(english);
        var remove = english.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("chipRemove"));
        Visual[] reading = [name, makeMain, remove];
        Assert.Equal(reading, english.GetVisualDescendants().Where(reading.Contains));

        makeMain.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("en, uk", session.Config.Profiles[0].Language);
        chips = LanguageChips(window);
        Assert.Equal(["en", "uk"], chips.Select(Code));
        Assert.Equal((true, true), (MainMark(chips[0]).IsEffectivelyVisible, MakeMain(chips[1]).IsEffectivelyVisible));
        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LanguagePicker");
        Assert.Same(picker, TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        window.Close();
    });

    // One language has no main one to speak of, so the hint goes with the chips.
    [Fact]
    public Task TheMainLanguageHintShowsOnlyWithSeveral() => fx.Ui(() =>
    {
        var (window, _) = Show();

        window.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemCount == 2).SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(Strings.SettingsProfilesLanguagesMainHint, VisibleTexts(window));
        window.Close();
    });

    // A picker that stays on its last pick cannot pick the same language again.
    [Fact]
    public Task PickingALanguageClearsThePicker() => fx.Ui(() =>
    {
        var (window, session) = Show();
        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LanguagePicker");

        picker.SelectedItem = picker.Items.OfType<Choice<string>>().Single(o => o.Value == "pl");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("uk, en, pl", session.Config.Profiles[0].Language);
        Assert.Null(picker.SelectedItem);
        window.Close();
    });
}
