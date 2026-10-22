using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Processing;

namespace Dictum.App.Tests.Settings;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ManualTimers _timers = new();

    /// <summary>What Help opened, in order.</summary>
    private readonly List<(DocsPage Page, string? Anchor)> _opened = [];

    public void Dispose() => _dir.Dispose();

    private void OpenDocs(DocsPage page, string? anchor) => _opened.Add((page, anchor));

    internal sealed class Page(string title, string anchor = DocsAnchors.Settings.General) : SettingsPage
    {
        public override string Title => title;

        public override string DocsAnchor => anchor;

        public List<string> Calls { get; } = [];

        public override void OnShown() => Calls.Add("shown");

        public override void OnHidden() => Calls.Add("hidden");

        public override void OnLink(DaemonLink link) => Calls.Add($"link {link.State}");

        public void OpenChild(SettingsPage child) => Navigate(child);

        public void Leave() => GoBack();
    }

    private (SettingsViewModel Model, Page General, Page System, SettingsSession Session, ConfigStore Store) Create()
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        var general = new Page("General");
        var system = new Page("System", DocsAnchors.Settings.System);
        var model = new SettingsViewModel(session, [(SettingsPageKey.General, general), (SettingsPageKey.System, system)], OpenDocs);
        return (model, general, system, session, store);
    }

    private SettingsSession Session()
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(ProfilesPageTests.Sample());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        return session;
    }

    private GeneralPage General(SettingsSession session) => new(session, new GeneralServices(
        new FakeChannel(), new FrameHub(TimeProvider.System), _timers, null, null, new FakeSounds(), TimeProvider.System, NullLog.Instance, IsLinux: false));

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

    [Fact]
    public void ClosingTheWindowHidesThePage()
    {
        var (model, general, _, _, _) = Create();
        model.Open(SettingsPageKey.General);

        model.Close();

        Assert.Null(model.Current);
        Assert.Equal(["shown", "hidden"], general.Calls);
    }

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

    // The version at the foot of the rail opens About Dictum, the rail's last page.
    [Fact]
    public void TheVersionOpensAboutDictum()
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        var general = new Page("General");
        var about = new Page("About Dictum", DocsAnchors.Settings.About);
        var model = new SettingsViewModel(session, [(SettingsPageKey.General, general), (SettingsPageKey.About, about)], OpenDocs);
        model.Open(SettingsPageKey.General);

        model.AboutCommand.Execute(null);

        Assert.Same(about, model.Selected);
        Assert.Same(about, model.Current);
    }

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

    private sealed class OwningPage : SettingsPage, IDisposable
    {
        public override string Title => "History";

        public override string DocsAnchor => DocsAnchors.Settings.History;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void DisposingTheWindowsModelDisposesThePagesThatOwnSomething()
    {
        var store = new ConfigStore(_dir.File("config.json"), NullLog.Instance);
        store.Save(new DictumConfig());
        var session = new SettingsSession(store, _timers, NullLog.Instance);
        session.Load();
        var history = new OwningPage();
        var model = new SettingsViewModel(session, [(SettingsPageKey.General, new Page("General")), (SettingsPageKey.History, history)], OpenDocs);

        model.Dispose();

        Assert.True(history.Disposed);
    }

    private sealed class LinkPage : SettingsPage
    {
        public override string Title => "Overlay";

        public override string DocsAnchor => DocsAnchors.Settings.Overlay;

        public DaemonLink? Last { get; private set; }

        public override void OnLink(DaemonLink link) => Last = link;
    }

    [Fact]
    public void UnsavedErrorsShowTheBanner()
    {
        var (model, _, _, session, _) = Create();

        session.Edit(c => c with { General = c.General with { HistorySize = -1 } });
        Assert.Equal(Strings.SettingsNotSaved, model.Banner);

        session.Edit(c => c with { General = c.General with { HistorySize = 5 } });
        Assert.Null(model.Banner);
    }

    [Fact]
    public void HelpOpensTheShownPagesSection()
    {
        var (model, _, _, _, _) = Create();
        model.Open(SettingsPageKey.System);

        model.HelpCommand.Execute(null);

        Assert.Equal([(DocsPage.Settings, DocsAnchors.Settings.System)], _opened);
    }

    // A sub-page keeps General selected in the rail, but Help opens the sub-page's own section.
    [Fact]
    public void HelpOnPastingOpensPastingNotGeneral()
    {
        var session = Session();
        var general = General(session);
        var model = new SettingsViewModel(session, [(SettingsPageKey.General, general)], OpenDocs);
        model.Open(SettingsPageKey.General);
        general.OpenPastingCommand.Execute(null);

        model.HelpCommand.Execute(null);

        Assert.Same(general, model.Selected);
        Assert.Equal([(DocsPage.Settings, DocsAnchors.Settings.Pasting)], _opened);
    }

    [Fact]
    public void HelpWithNoPageShownOpensTheTopOfTheSettingsReference()
    {
        var (model, _, _, _, _) = Create();

        model.HelpCommand.Execute(null);

        Assert.Equal([(DocsPage.Settings, (string?)null)], _opened);
    }

    // Each page opens its own section of the Settings reference: the sections the docs have, one page each.
    [Fact]
    public void EveryPageLinksToItsOwnSectionOfTheSettingsReference()
    {
        var session = Session();
        var general = General(session);
        var channel = new FakeChannel();
        var secrets = new FakeSecrets();
        var runner = new FakeRunner();
        var paths = new AppPaths(_dir.File("config"), _dir.File("data"));
        var row = new SystemRow("Tray icon", HealthLevel.Ok, "Always visible");
        using var processors = new ProcessorsPage(session, new ProcessorsServices(runner, secrets, new PromptLibrary(_dir.File("prompts")), _timers, NullLog.Instance));
        using var history = new HistoryPage(session, _dir.File("history.jsonl"), new WindowClipboard(() => null), runner, NullLog.Instance);
        SettingsPage[] pages =
        [
            general,
            general.Overlay,
            general.Pasting,
            new ProfilesPage(session, channel),
            new SpeechProvidersPage(session, new SpeechServices(channel, new FrameHub(TimeProvider.System), _timers, secrets,
                _ => [], _ => true, _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance)),
            processors,
            new ModelsPage(session, new ModelsServices(paths, ModelsPageTests.TestCatalog(), new FakeDownloads(),
                () => Task.FromResult(ModelsPageTests.IrisXe), () => long.MaxValue, ModelsPageTests.Memory, 8, new SpeedMeasurements(),
                channel, _timers, _ => Task.CompletedTask, TimeProvider.System, NullLog.Instance)),
            new SystemPage(new SystemServices(channel, new StatusViewModel("c", "l", () => { }), () => row, () => row, _timers, TimeProvider.System,
                "Windows 11", OpenDocs)),
            history,
            new AboutPage(new AboutServices("0.4.0", "Windows 11 (10.0.26200), x64", "win-x64", null, () => Task.FromResult(ModelsPageTests.IrisXe),
                new WindowClipboard(() => null), OpenDocs, _ => { }, NullLog.Instance)),
        ];
        var sections = DocsLinks.Targets.Where(t => t is { Page: DocsPage.Settings, Anchor: not null }).Select(t => t.Anchor!);

        Assert.Equal(
            [
                DocsAnchors.Settings.General, DocsAnchors.Settings.Overlay, DocsAnchors.Settings.Pasting, DocsAnchors.Settings.Profiles,
                DocsAnchors.Settings.SpeechRecognition, DocsAnchors.Settings.Processors, DocsAnchors.Settings.Models,
                DocsAnchors.Settings.System, DocsAnchors.Settings.History, DocsAnchors.Settings.About,
            ],
            pages.Select(p => p.DocsAnchor));
        Assert.Equal(sections.Order(StringComparer.Ordinal), pages.Select(p => p.DocsAnchor).Order(StringComparer.Ordinal));
    }
}
