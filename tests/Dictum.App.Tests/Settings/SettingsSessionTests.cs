using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Settings;

public sealed class SettingsSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dictum-settings", Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTimers _timers = new();

    public SettingsSessionTests() => Directory.CreateDirectory(_dir);

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

    private static DictumConfig HistoryOf(int size) => new() { General = new GeneralSettings { HistorySize = size } };

    private static Func<DictumConfig, DictumConfig> SetHistory(int size) => c => c with { General = c.General with { HistorySize = size } };

    private SettingsSession Open(DictumConfig? saved = null)
    {
        if (saved is not null)
        {
            Store.Save(saved);
        }

        var session = new SettingsSession(Store, _timers, NullLog.Instance);
        session.Load();
        return session;
    }

    private int OnDisk() => Store.Load().General.HistorySize;

    [Fact]
    public void TheConfigOnDiskIsLoaded()
    {
        var session = Open(HistoryOf(150));

        Assert.Equal(150, session.Config.General.HistorySize);
        Assert.True(session.CanEdit);
        Assert.Null(session.Problem);
    }

    [Fact]
    public void WithoutAConfigThePagesCannotEdit()
    {
        var session = Open();

        Assert.False(session.CanEdit);
        Assert.Equal(Strings.SettingsNoConfig, session.Problem);

        session.Edit(SetHistory(5));
        Assert.Equal(0, _timers.Pending);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public void AnInvalidFileIsAProblem()
    {
        File.WriteAllText(Store.Path, """{ "schemaVersion": 1, "general": { "historySize": -1 } }""");

        var session = Open();

        Assert.False(session.CanEdit);
        Assert.NotNull(session.Problem);
        Assert.Contains(session.Issues, i => i.Path == "general.historySize");
    }

    [Fact]
    public void AnEditIsSavedAfterTheDelay()
    {
        var session = Open(HistoryOf(200));

        session.Edit(SetHistory(300));

        Assert.Equal(300, session.Config.General.HistorySize);
        Assert.Equal(200, OnDisk());
        Assert.Equal(SettingsSession.SaveDelay, _timers.LastDelay);

        _timers.Fire();

        Assert.Equal(300, OnDisk());
    }

    // Review focus 1.
    [Fact]
    public void EditsWithinTheDelaySaveOnce()
    {
        var session = Open(HistoryOf(100));
        var saves = 0;
        session.Saved += () => saves++;

        foreach (var size in new[] { 2, 20, 200 })
        {
            session.Edit(SetHistory(size));
        }

        Assert.Equal(1, _timers.Pending);
        _timers.Fire();
        Assert.Equal(1, saves);
        Assert.Equal(200, OnDisk());
    }

    // Review focus 1.
    [Fact]
    public void AnInvalidEditIsShownAndNotSaved()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));

        session.Edit(SetHistory(-1));

        Assert.Equal("must be zero or positive", session.IssueAt("general.historySize"));
        Assert.Equal(0, _timers.Pending);
        _timers.Fire();
        Assert.Equal(100, OnDisk());

        session.Edit(SetHistory(50));
        Assert.Null(session.IssueAt("general.historySize"));
        _timers.Fire();
        Assert.Equal(50, OnDisk());
    }

    [Fact]
    public void FlushSavesAPendingEditNow()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));

        session.Flush();

        Assert.Equal(300, OnDisk());
        Assert.Equal(0, _timers.Pending);
    }

    // Review focus 4.
    [Fact]
    public void OwnSavesAreNotExternalEdits()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));
        _timers.Fire();
        var changes = 0;
        session.Changed += () => changes++;

        session.Edit(SetHistory(400));
        session.OnFileChanged(Store.Load());

        Assert.Equal(400, session.Config.General.HistorySize);
        Assert.Equal(1, changes);
        Assert.Equal(1, _timers.Pending);
    }

    [Fact]
    public void AnExternalEditReplacesTheCopyAndDropsPendingEdits()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));

        Store.Save(HistoryOf(700));
        session.OnFileChanged(Store.Load());

        Assert.Equal(700, session.Config.General.HistorySize);
        Assert.Equal(0, _timers.Pending);
        _timers.Fire();
        Assert.Equal(700, OnDisk());
    }

    [Fact]
    public void AnInvalidFileFromOutsideStopsEditingUntilItIsFixed()
    {
        var session = Open(HistoryOf(100));

        session.OnFileInvalid(new ConfigException("Config is invalid: broken"));
        Assert.False(session.CanEdit);

        session.OnFileChanged(HistoryOf(120));
        Assert.True(session.CanEdit);
        Assert.Equal(120, session.Config.General.HistorySize);
    }

    [Fact]
    public void ASaveThatFailsIsReported()
    {
        var session = Open(HistoryOf(100));
        Directory.CreateDirectory(Store.Path + ".tmp");

        session.Edit(SetHistory(300));
        _timers.Fire();

        Assert.NotNull(session.SaveError);
        Assert.Equal(100, OnDisk());
    }

    [Fact]
    public void EditsWorkOnACopy()
    {
        var session = Open(HistoryOf(100));
        var before = session.Config;

        session.Edit(c =>
        {
            c.General.TerminalApps.Add("my-term");
            return c;
        });

        Assert.DoesNotContain("my-term", before.General.TerminalApps);
        Assert.Contains("my-term", session.Config.General.TerminalApps);
    }


    // Final review C1: an edit back to what is on disk leaves nothing to save.
    [Fact]
    public void AnEditBackToTheValueOnDiskSavesNothing()
    {
        var session = Open(HistoryOf(100));
        var saves = 0;
        session.Saved += () => saves++;

        session.Edit(SetHistory(10));
        session.Edit(SetHistory(100));

        Assert.Equal(0, _timers.Pending);
        session.Flush();
        Assert.Equal(0, saves);
    }

    // Final review C1: a good save changes nothing the pages show, so it must not reload them.
    [Fact]
    public void AGoodSaveDoesNotReloadThePages()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));
        var changes = 0;
        session.Changed += () => changes++;

        _timers.Fire();

        Assert.Equal(0, changes);
        Assert.Equal(300, OnDisk());
    }

    [Fact]
    public void OnDiskIsTheLastFileKnownToBeThere()
    {
        var session = Open(HistoryOf(100));

        session.Edit(SetHistory(300));
        Assert.Equal(100, session.OnDisk.General.HistorySize);

        _timers.Fire();
        Assert.Equal(300, session.OnDisk.General.HistorySize);
    }

    // Final review I2a.
    [Fact]
    public void AnOutsideRevertToOurLastSaveIsApplied()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));
        _timers.Fire();
        Store.Save(HistoryOf(700));
        session.OnFileChanged(Store.Load());

        Store.Save(HistoryOf(300));
        session.OnFileChanged(Store.Load());

        Assert.Equal(300, session.Config.General.HistorySize);
    }

    // Final review I2b.
    [Fact]
    public void AFileFixedBackToOurLastSaveCanBeEditedAgain()
    {
        var session = Open(HistoryOf(100));
        session.Edit(SetHistory(300));
        _timers.Fire();
        session.OnFileInvalid(new ConfigException("Config is invalid: broken"));

        session.OnFileChanged(Store.Load());

        Assert.True(session.CanEdit);
        Assert.Null(session.Problem);
    }
}
