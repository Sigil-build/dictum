using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Processing;

namespace Dictum.App.Tests.Views;

public sealed class ProcessorsViewTests(AvaloniaFixture fx) : IDisposable
{
    private static readonly string[] TypedLetters = ["м", "е", "й", "н"];
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private (Window Window, ProcessorsPage Page, SettingsSession Session) Show()
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(ProfilesPageTests.Sample());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new ProcessorsPage(session, new ProcessorsServices(new FakeRunner(), new FakeSecrets(), new PromptLibrary(_dir.File("prompts")),
            new ManualTimers(), NullLog.Instance));
        var window = new Window { Content = new ViewLocator().Build(page), Width = 1100, Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, page, session);
    }

    private static List<TextBox> FindBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("ruleFind") && t.IsEffectivelyVisible)];

    private static Border Banner(Window window) => window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("banner"));

    private static ComboBox AddToPicker(Window window) => window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AddToPicker");

    private static List<string?> VisibleTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    private static void Tab(Window window)
    {
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    // The warning takes the caption's place under the header: Tab and a screen reader reach it after Remove, then its
    // picker, then the editor.
    [Fact]
    public Task AProcessorInNoChainShowsTheWarningInReadingOrder() => fx.Ui(() =>
    {
        var (window, page, _) = Show();
        Assert.False(Banner(window).IsEffectivelyVisible);
        Assert.Contains("Used by: Work", VisibleTexts(window));

        page.Selected = page.Processors.Single(p => p.Id == "filler");
        Dispatcher.UIThread.RunJobs();

        var picker = AddToPicker(window);
        Assert.True(Banner(window).IsEffectivelyVisible);
        Assert.True(picker.IsEffectivelyVisible);
        Assert.Contains(Strings.SettingsProcessorsNoChain, VisibleTexts(window));
        Assert.DoesNotContain(Strings.SettingsProcessorsUnused, VisibleTexts(window));
        Assert.Equal(["Work", "Home"], picker.Items.OfType<Choice<string>>().Select(c => c.Label));

        var remove = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.SettingsProcessorsRemove));
        var warning = Banner(window).GetVisualDescendants().OfType<TextBlock>().Single();
        var firstRule = FindBoxes(window)[0];
        Visual[] reading = [remove, warning, picker, firstRule];
        Assert.Equal(reading, window.GetVisualDescendants().Where(reading.Contains));

        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        remove.Focus();
        Tab(window);
        Assert.Same(picker, focus.GetFocusedElement());
        Tab(window);
        Assert.Same(firstRule, focus.GetFocusedElement());
        window.Close();
    });

    [Fact]
    public Task PickingAProfileInTheWarningAddsTheProcessorToItsChain() => fx.Ui(() =>
    {
        var (window, page, session) = Show();
        page.Selected = page.Processors.Single(p => p.Id == "filler");
        Dispatcher.UIThread.RunJobs();
        var picker = AddToPicker(window);

        picker.SelectedItem = picker.Items.OfType<Choice<string>>().Single(c => c.Value == "home");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["cleanup → terms", "filler"], session.Config.Profiles.Select(p => string.Join(" → ", p.Pipeline)));
        Assert.Null(picker.SelectedItem);
        Assert.False(Banner(window).IsEffectivelyVisible);
        Assert.Contains("Used by: Home", VisibleTexts(window));
        window.Close();
    });

    [Fact]
    public Task TheLlmEditorShowsThePrompt() => fx.Ui(() =>
    {
        var (window, page, _) = Show();

        Assert.IsType<ProcessorsView>(window.Content);
        var prompt = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptBox");
        Assert.Equal(page.Llm.Prompt.Text, prompt.Text);
        window.Close();
    });

    // Typing in the blank row adds a rule and a new blank row.
    [Fact]
    public Task TypingInTheBlankRowOfTheTableAddsARule() => fx.Ui(() =>
    {
        var (window, page, session) = Show();
        page.Selected = page.Processors.Single(p => p.Id == "terms");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, FindBoxes(window).Count);

        var blank = FindBoxes(window)[^1];
        blank.Focus();
        window.KeyTextInput("мейн");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["кубер", "мейн"], ((ReplaceProcessorConfig)session.Config.Processors["terms"]).Rules.Select(r => r.From));
        Assert.Equal(3, FindBoxes(window).Count);
        Assert.Equal("мейн", FindBoxes(window)[1].Text);
        window.Close();
    });


    // Real typing sends one key per event; the box must keep focus as the blank row becomes a rule.
    [Fact]
    public Task TypingOneKeyAtATimeInTheBlankRowKeepsFocus() => fx.Ui(() =>
    {
        var (window, page, session) = Show();
        page.Selected = page.Processors.Single(p => p.Id == "terms");
        Dispatcher.UIThread.RunJobs();
        var blank = FindBoxes(window)[^1];
        blank.Focus();

        foreach (var key in TypedLetters)
        {
            window.KeyTextInput(key);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(blank, TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        }

        Assert.Equal(["кубер", "мейн"], ((ReplaceProcessorConfig)session.Config.Processors["terms"]).Rules.Select(r => r.From));
        window.Close();
    });
}
