using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Settings;
using Dictum.App.Tests.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Views;

public sealed class ProcessorsViewTests(AvaloniaFixture fx) : IDisposable
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

    private (Window Window, ProcessorsPage Page, SettingsSession Session) Show()
    {
        Directory.CreateDirectory(_dir);
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(ProfilesPageTests.Sample());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var page = new ProcessorsPage(session, new ProcessorsServices(new FakeRunner(), new FakeSecrets(), new PromptFiles(Path.Combine(_dir, "prompts")),
            new ManualTimers(), NullLog.Instance));
        var window = new Window { Content = new ViewLocator().Build(page), Width = 1100, Height = 1400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, page, session);
    }

    private static List<TextBox> FindBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("ruleFind") && t.IsEffectivelyVisible)];

    [Fact]
    public Task TheLlmEditorShowsThePrompt() => fx.Ui(() =>
    {
        var (window, page, _) = Show();

        Assert.IsType<ProcessorsView>(window.Content);
        var prompt = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptBox");
        Assert.Equal(page.Llm.PromptText, prompt.Text);
        window.Close();
    });

    // Review focus 1, in the real table: typing in the blank row adds a rule and a new blank row.
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


    // Final review C1: real typing sends one key per event; the box must keep focus as the blank row becomes a rule.
    [Fact]
    public Task TypingOneKeyAtATimeInTheBlankRowKeepsFocus() => fx.Ui(() =>
    {
        var (window, page, session) = Show();
        page.Selected = page.Processors.Single(p => p.Id == "terms");
        Dispatcher.UIThread.RunJobs();
        var blank = FindBoxes(window)[^1];
        blank.Focus();

        foreach (var key in new[] { "м", "е", "й", "н" })
        {
            window.KeyTextInput(key);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(blank, TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        }

        Assert.Equal(["кубер", "мейн"], ((ReplaceProcessorConfig)session.Config.Processors["terms"]).Rules.Select(r => r.From));
        window.Close();
    });
}
