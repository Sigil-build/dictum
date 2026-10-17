using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();

    public SettingsViewModelTests() => Directory.CreateDirectory(_dir);

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

    internal sealed class Page(string title) : SettingsPage
    {
        public override string Title => title;

        public List<string> Calls { get; } = [];

        public override void OnShown() => Calls.Add("shown");

        public override void OnHidden() => Calls.Add("hidden");

        public override void OnLink(DaemonLink link) => Calls.Add($"link {link.State}");

        public void OpenChild(SettingsPage child) => Navigate(child);

        public void Leave() => GoBack();
    }

    private (SettingsViewModel Model, Page General, Page System, SettingsSession Session, ConfigStore Store) Create()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        var general = new Page("General");
        var system = new Page("System");
        var model = new SettingsViewModel(session, [(SettingsPageKey.General, general), (SettingsPageKey.System, system)]);
        return (model, general, system, session, store);
    }

    [Fact]
    public void NothingIsShownUntilAPageIsOpened()
    {
        var (model, general, _, _, _) = Create();

        Assert.Null(model.Current);
        Assert.Empty(general.Calls);
        Assert.Equal(["General", "System"], model.Pages.Select(p => p.Title));
    }

    [Fact]
    public void OpeningAPageShowsItAndHidesThePreviousOne()
    {
        var (model, general, system, _, _) = Create();

        model.Open(SettingsPageKey.General);
        model.Open(SettingsPageKey.System);

        Assert.Same(system, model.Current);
        Assert.Same(system, model.Selected);
        Assert.Equal(["shown", "hidden"], general.Calls);
        Assert.Equal(["shown"], system.Calls);
    }

    [Fact]
    public void OpeningTheShownPageAgainChangesNothing()
    {
        var (model, general, _, _, _) = Create();

        model.Open(SettingsPageKey.General);
        model.Open(SettingsPageKey.General);

        Assert.Equal(["shown"], general.Calls);
    }

    [Fact]
    public void ASubPageKeepsItsParentSelectedAndGoesBack()
    {
        var (model, general, _, _, _) = Create();
        var child = new Page("Overlay");
        model.Open(SettingsPageKey.General);

        general.OpenChild(child);

        Assert.Same(child, model.Current);
        Assert.Same(general, model.Selected);

        child.Leave();

        Assert.Same(general, model.Current);
        Assert.Equal(["shown", "hidden"], child.Calls);
        Assert.Equal(["shown", "hidden", "shown"], general.Calls);
    }

    [Fact]
    public void OpeningARailPageFromASubPageLeavesTheSubPage()
    {
        var (model, general, _, _, _) = Create();
        var child = new Page("Overlay");
        model.Open(SettingsPageKey.General);
        general.OpenChild(child);

        model.Open(SettingsPageKey.General);

        Assert.Same(general, model.Current);
        Assert.Equal(["shown", "hidden"], child.Calls);
    }

    // Review focus 5.
    [Fact]
    public void ClosingTheWindowHidesThePage()
    {
        var (model, general, _, _, _) = Create();
        model.Open(SettingsPageKey.General);

        model.Close();

        Assert.Null(model.Current);
        Assert.Equal(["shown", "hidden"], general.Calls);
    }

    // Review focus 2.
    [Fact]
    public void ClosingTheWindowSavesPendingEdits()
    {
        var (model, _, _, session, store) = Create();
        session.Edit(c => c with { General = c.General with { HistorySize = 42 } });

        model.Close();

        Assert.Equal(42, store.Load().General.HistorySize);
    }

    [Fact]
    public void TheVersionNamesTheApp() => Assert.StartsWith("Dictum ", Create().Model.Version);

    [Fact]
    public void TheBannerShowsWhyPagesCannotEditAndClearsWhenTheyCan()
    {
        var (model, _, _, session, store) = Create();
        Assert.Null(model.Banner);

        session.OnFileInvalid(new ConfigException("Config is invalid: broken"));
        Assert.Equal("Config is invalid: broken", model.Banner);

        session.OnFileChanged(store.Load());
        Assert.Null(model.Banner);
    }

    [Fact]
    public void LinkChangesReachEveryPageAndOpenedSubPages()
    {
        var (model, general, system, _, _) = Create();
        var child = new LinkPage();
        model.Open(SettingsPageKey.General);
        general.OpenChild(child);
        var link = new DaemonLink(DaemonLinkState.Restarting);

        model.OnLink(link);

        Assert.Equal(["shown", "hidden", "link Restarting"], general.Calls);
        Assert.Equal(["link Restarting"], system.Calls);
        Assert.Same(link, child.Last);
    }

    private sealed class LinkPage : SettingsPage
    {
        public override string Title => "Overlay";

        public DaemonLink? Last { get; private set; }

        public override void OnLink(DaemonLink link) => Last = link;
    }

    // Review focus 3.
    [Fact]
    public void UnsavedErrorsShowTheBanner()
    {
        var (model, _, _, session, _) = Create();

        session.Edit(c => c with { General = c.General with { HistorySize = -1 } });
        Assert.Equal(Strings.SettingsNotSaved, model.Banner);

        session.Edit(c => c with { General = c.General with { HistorySize = 5 } });
        Assert.Null(model.Banner);
    }
}
