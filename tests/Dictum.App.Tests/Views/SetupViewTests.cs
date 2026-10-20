using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Tests.Setup;
using Dictum.App.ViewModels.Setup;
using Dictum.App.Views.Setup;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;

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

    private static Border LanguageRow(Window window, string code) =>
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("pickRow") && b.DataContext is LanguageRow row && row.Code == code);

    private static Border MainBadge(Border row) => row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("badge"));

    private static Button MakeMain(Border row) => row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("makeMain"));

    private static (bool Badge, bool MakeMain) Shown(Border row) => (MainBadge(row).IsEffectivelyVisible, MakeMain(row).IsEffectivelyVisible);

    // The main language wears a badge and the lead says what it means; another picked row offers "Make main", which moves
    // the badge to it and leaves the focus on its row's box.
    [Fact]
    public Task PickedRowsShowTheMainLanguageAndMakeMain() => fx.Ui(() =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);
        window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Українська").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        var (english, ukrainian, polish) = (LanguageRow(window, "en"), LanguageRow(window, "uk"), LanguageRow(window, "pl"));
        Assert.Equal((true, false), Shown(english));
        Assert.Equal((false, true), Shown(ukrainian));
        Assert.Equal((false, false), Shown(polish));
        Assert.Equal(Strings.SetupLanguagesMain, MainBadge(english).GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.SetupLanguagesMainLead && t.IsEffectivelyVisible);

        var box = ukrainian.GetVisualDescendants().OfType<CheckBox>().Single();
        var code = ukrainian.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "uk");
        Visual[] reading = [box, MakeMain(ukrainian), code];
        Assert.Equal(reading, ukrainian.GetVisualDescendants().Where(reading.Contains));

        MakeMain(ukrainian).Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("uk, en", wizard.Languages.Setting);
        Assert.Equal((false, true), Shown(english));
        Assert.Equal((true, false), Shown(ukrainian));
        Assert.Same(box, TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        window.Close();
    });

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

    // A long choice label wraps in the 640 px dialog instead of being cut off.
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

    // Tab follows reading order: the step's search, its list, the option under the list, then Next.
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

    // A picked row's "Make main" comes right after its box.
    [Fact]
    public Task TabReachesMakeMainAfterItsRow() => fx.UiAsync(() =>
    {
        var wizard = new SetupViewModel(_setup.Services());
        wizard.Languages.Rows.Single(r => r.Code == "pl").IsChecked = true;
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
        Assert.Equal(["search", "row en", "row pl", Strings.SettingsProfilesLanguageMakeMain, "detect any", "Next"], order);
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

    // Step 8 gives its try-it box the focus, so the first dictation lands there, and shows the daemon's line.
    [Fact]
    public Task StepEightFocusesTheTryBoxAndShowsTheDaemon() => fx.UiAsync(async () =>
    {
        _setup.InstallAll();
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);
        while (wizard.Index < 7)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }

        var profile = _setup.Store.Load().Profiles[0];
        wizard.OnLink(new DaemonLink(DaemonLinkState.Connected, new DaemonStatus(
            nameof(PipelineState.Idle), null, [new ProfileStatus(profile.Id, profile.Hotkey, "PushToTalk", true)], "hook", "paste", 4242, WarmedUp: true)));
        Dispatcher.UIThread.RunJobs();

        var focused = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement();
        Assert.Equal("TryBox", Assert.IsType<TextBox>(focused).Name);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Dictum is running. Try it in the box below.");
        window.Close();
    });

    // A failed start shows Restart, which reaches the supervisor.
    [Fact]
    public Task StepEightOffersRestartWhenTheDaemonFailed() => fx.UiAsync(async () =>
    {
        _setup.InstallAll();
        var wizard = new SetupViewModel(_setup.Services());
        var window = Show(wizard);
        while (wizard.Index < 7)
        {
            await wizard.NextCommand.ExecuteAsync(null);
        }

        wizard.OnLink(new DaemonLink(DaemonLinkState.Failed, Problem: "The daemon exited while starting (code 1)."));
        Dispatcher.UIThread.RunJobs();
        var restart = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Restart daemon"));
        restart.Command!.Execute(null);

        Assert.Equal(1, _setup.Restarts);
        window.Close();
    });
}
