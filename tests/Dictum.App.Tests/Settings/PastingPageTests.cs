using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class PastingPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();

    public PastingPageTests() => Directory.CreateDirectory(_dir);

    private ConfigStore Store => new(Path.Combine(_dir, "config.json"), NullLog.Instance);

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

    private (PastingPage Page, SettingsSession Session) Open(bool linux = true)
    {
        Store.Save(new DictumConfig());
        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        return (new PastingPage(session, "General", linux), session);
    }

    [Fact]
    public void TheRowsShowTheConfig()
    {
        var (page, _) = Open();

        Assert.Equal(150, page.PasteDelay);
        Assert.Equal(new GeneralSettings().TerminalApps, page.TerminalApps.Select(a => a.Text));
        Assert.Equal(InjectorBackend.Auto, page.Injector?.Value);
        Assert.Equal("‹ General", page.BackLabel);
        Assert.Equal(Strings.SettingsPasting, page.Title);
    }

    [Fact]
    public void TheDelayIsSavedAndAnEmptiedFieldSavesNothingItPassedThrough()
    {
        var (page, session) = Open();

        page.PasteDelay = 300;
        _timers.Fire();
        Assert.Equal(300, Store.Load().General.PasteDelayMs);

        page.PasteDelay = 30;
        page.PasteDelay = null;
        session.Flush();

        Assert.Equal(300, Store.Load().General.PasteDelayMs);
        Assert.Equal(Strings.SettingsNumberRequired, page.PasteDelayError);
        Assert.Null(page.PasteDelay);
    }

    [Fact]
    public void TerminalAppsSplitAndRemove()
    {
        var (page, session) = Open();

        Assert.Equal("wezterm", page.AddApps("my-term, kitty\nwezterm"));
        Assert.Contains("my-term", session.Config.General.TerminalApps);
        Assert.Single(session.Config.General.TerminalApps, a => a == "kitty");

        page.TerminalApps.Single(a => a.Text == "my-term").Remove.Execute(null);
        Assert.DoesNotContain("my-term", session.Config.General.TerminalApps);
    }

    [Fact]
    public void TheBackendIsSavedAndShownOnlyOnLinux()
    {
        var (page, session) = Open();

        page.Injector = page.Injectors.Single(i => i.Value == InjectorBackend.Wtype);

        Assert.Equal(InjectorBackend.Wtype, session.Config.General.Injector);
        Assert.True(page.ShowsInjector);
        Assert.False(Open(linux: false).Page.ShowsInjector);
    }

    [Fact]
    public void BackLeavesTheSubPage()
    {
        var (page, _) = Open();
        var back = 0;
        page.BackRequested += () => back++;

        page.BackCommand.Execute(null);

        Assert.Equal(1, back);
    }

    [Fact]
    public void GeneralOpensTheSubPage()
    {
        var (_, session) = Open();
        var general = new GeneralPage(session, new GeneralServices(new FakeChannel(), new FrameHub(TimeProvider.System), _timers, null, null,
            new FakeSounds(), TimeProvider.System, NullLog.Instance));
        SettingsPage? opened = null;
        general.NavigateRequested += page => opened = page;

        general.OpenPastingCommand.Execute(null);

        Assert.Same(general.Pasting, opened);
    }
}
