using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Resources;
using Dictum.App.Tests.Setup;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Setup;

namespace Dictum.App.Tests.Views;

public sealed class SetupViewTests(AvaloniaFixture fx) : IDisposable
{
    private readonly SetupFixture _setup = new();

    public void Dispose() => _setup.Dispose();

    private static Window Show(SetupViewModel wizard)
    {
        var window = new Window { Content = new ViewLocator().Build(wizard), Width = 640, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string? Heading(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("h1") && t.IsEffectivelyVisible)?.Text;

    [Fact]
    public Task EveryStepShowsItsHeading() => fx.UiAsync(async () =>
    {
        _setup.InstallAll();
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);
        Assert.IsType<SetupView>(window.Content);

        string[] headings = ["Which languages do you dictate in?", "Speech model", "Text clean-up", "Your words", "Start Dictum at login?", "What will be downloaded"];
        foreach (var heading in headings)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(heading, Heading(window));
            if (wizard.Index < 5)
            {
                await wizard.NextCommand.ExecuteAsync(null);
            }
        }

        window.Close();
    });

    [Fact]
    public Task TheStripMarksDoneAndCurrentSteps() => fx.UiAsync(async () =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var bars = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("stripBar")).ToList();
        Assert.Equal([true, true, true, false, false, false, false, false], bars.Select(b => b.Classes.Contains("on")));
        window.Close();
    });

    [Fact]
    public Task TickingALanguageRowPicksIt() => fx.Ui(() =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);

        var polish = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Polski");
        polish.IsChecked = true;

        Assert.Equal("en, pl", wizard.Languages.Setting);
        window.Close();
    });

    // Review focus 4.
    [Fact]
    public Task EscClosesTheWindowAndKeepsTheStep() => fx.UiAsync(async () =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        await wizard.NextCommand.ExecuteAsync(null);
        var window = new SetupWindow { DataContext = wizard };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(SetupWindow.Open);
        Assert.Equal(1, wizard.Index);
    });

    [Fact]
    public Task EnterGoesToTheNextStepWhenItCan() => fx.Ui(() =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        var window = new SetupWindow { DataContext = wizard };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, wizard.Index);
        window.Close();
    });

    // Final review I4: a long choice label wraps in the 640 px dialog instead of being cut off.
    [Fact]
    public Task LongChoiceLabelsWrap() => fx.UiAsync(async () =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var label = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.SetupCleanupServer);

        Assert.True(label.TextLayout.TextLines.Count > 1, $"{label.TextLayout.TextLines.Count} line(s)");
        window.Close();
    });

    // Final review M1: Tab follows reading order (spec 14): the step's search, its list, the option under the list, then Next.
    [Fact]
    public Task TabFollowsReadingOrder() => fx.UiAsync(() =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        wizard.Languages.Search = "Polish";
        var window = new SetupWindow { DataContext = wizard };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        var order = new List<string>();
        while (order.Count < 20 && order.LastOrDefault() != "Next")
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            order.Add(Describe(focus.GetFocusedElement()));
        }

        window.Close();
        Assert.Equal(["search", "row en", "row pl", "detect any", "Next"], order);
        return Task.CompletedTask;
    });

    private static string Describe(IInputElement? element) => element switch
    {
        TextBox => "search",
        CheckBox { DataContext: LanguageRow row } => "row " + row.Code,
        CheckBox { DataContext: SetupLanguagesStep } => "detect any",
        Button { Content: string text } => text,
        _ => element?.GetType().Name ?? "none",
    };
}
