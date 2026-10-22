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
using Dictum.Core.Docs;

namespace Dictum.App.Tests.Views;

public sealed class SettingsWindowTests(AvaloniaFixture fx) : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly List<(DocsPage Page, string? Anchor)> _opened = [];

    public void Dispose() => _dir.Dispose();

    private SettingsViewModel Model(out SettingsViewModelTests.Page general, out SettingsViewModelTests.Page system)
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        general = new SettingsViewModelTests.Page("General");
        system = new SettingsViewModelTests.Page("System");
        return new SettingsViewModel(session, [(SettingsPageKey.General, general), (SettingsPageKey.System, system)], (page, anchor) => _opened.Add((page, anchor)));
    }

    // Help sits beside the version at the bottom of the rail and opens the shown page's section.
    [Fact]
    public Task HelpBesideTheVersionOpensTheShownPagesSection() => fx.Ui(() =>
    {
        var model = Model(out _, out _);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);

        var help = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.SettingsHelp));
        Assert.True(help.IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.Version && t.IsEffectivelyVisible);
        help.Command!.Execute(null);

        Assert.Equal([(DocsPage.Settings, DocsAnchors.Settings.General)], _opened);
        window.Close();
    });

    // The version at the foot of the rail is a link to About Dictum, the rail's last page.
    [Fact]
    public Task TheVersionOpensAboutDictum() => fx.Ui(() =>
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, new ManualTimers(), NullLog.Instance);
        session.Load();
        var about = new SettingsViewModelTests.Page("About Dictum", DocsAnchors.Settings.About);
        var model = new SettingsViewModel(session,
            [(SettingsPageKey.General, new SettingsViewModelTests.Page("General")), (SettingsPageKey.About, about)], (_, _) => { });
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);

        var version = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, model.Version));
        Assert.True(version.IsEffectivelyVisible);
        version.Command!.Execute(null);

        Assert.Same(about, model.Current);
        window.Close();
    });

    // Tab follows the reading order: the rail first, then the version and Help at its foot.
    [Fact]
    public Task TabReachesTheRailBeforeTheVersionAndHelp() => fx.Ui(() =>
    {
        var model = Model(out _, out _);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);
        Dispatcher.UIThread.RunJobs();

        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;
        var order = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            order.Add(Describe(focus.GetFocusedElement()));
        }

        window.Close();
        Assert.Equal(["rail General", model.Version, Strings.SettingsHelp], order);
    });

    private static string Describe(IInputElement? element) => element switch
    {
        ListBoxItem { DataContext: SettingsPage page } => "rail " + page.Title,
        Button { Content: string text } => text,
        _ => element?.GetType().Name ?? "none",
    };

    [Fact]
    public Task TheRailListsThePagesAndSelectingOneShowsIt() => fx.Ui(() =>
    {
        var model = Model(out _, out var system);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);

        var rail = window.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(2, rail.ItemCount);

        rail.SelectedIndex = 1;

        Assert.Same(system, model.Current);
        window.Close();
    });

    [Fact]
    public Task ClosingTheWindowClosesTheModel() => fx.Ui(() =>
    {
        var model = Model(out var general, out _);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.Open(SettingsPageKey.General);

        window.Close();

        Assert.Equal(["shown", "hidden"], general.Calls);
        Assert.DoesNotContain(window, SettingsWindow.Open);
    });

    [Fact]
    public Task TheWindowHasTheDesignSizesAndAnIcon() => fx.Ui(() =>
    {
        var window = new SettingsWindow { DataContext = Model(out _, out _) };

        Assert.Equal((960, 640, 800, 560), (window.Width, window.Height, window.MinWidth, window.MinHeight));
        Assert.NotNull(window.Icon);
    });
}
