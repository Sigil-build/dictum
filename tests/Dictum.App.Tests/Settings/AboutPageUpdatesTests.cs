using System.Globalization;
using System.Text;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Tests.Updates;
using Dictum.App.Updates;
using Dictum.App.ViewModels.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Setup;

namespace Dictum.App.Tests.Settings;

/// <summary>About Dictum's update state and Check now, over a real checker on a fake GitHub.</summary>
public sealed class AboutPageUpdatesTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeReleases _releases = new();
    private readonly ManualTimers _timers = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero));
    private readonly List<Uri> _urls = [];
    private UpdateChecker? _checker;

    public void Dispose()
    {
        _checker?.Dispose();
        _dir.Dispose();
    }

    private UpdateChecker Checker() =>
        _checker ??= new UpdateChecker(_releases, new UpdateCheckFile(_dir.File("update-check.json"), NullLog.Instance), _timers, _clock, "0.4.0", NullLog.Instance);

    private AboutPage Page(bool updates = true) => new(new AboutServices(
        "0.4.0", "Windows 11 (10.0.26200), x64", "win-x64", null, () => Task.FromResult(new HardwareInfo(null, 0)), new WindowClipboard(() => null),
        (_, _) => { }, _urls.Add, NullLog.Instance, updates ? Checker() : null));

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, CompositeFormat.Parse(format), args);

    private string CheckedAt => Format(Strings.SettingsAboutLastChecked, _clock.GetUtcNow().ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    [Fact]
    public void WithoutAnUpdateCheckThereIsNoUpdateState() => Assert.False(Page(updates: false).HasUpdates);

    [Fact]
    public void BeforeAnyCheckItSaysSo()
    {
        using var page = Page();

        Assert.True(page.HasUpdates);
        Assert.Equal((Strings.SettingsAboutNotChecked, null, null, false), (page.UpdateState, page.LastChecked, page.UpdateFailure, page.IsUpdateAvailable));
    }

    [Fact]
    public async Task CheckNowShowsANewerVersionAndOpensItsPage()
    {
        _releases.Next = FakeReleases.Found("v0.5.0");
        using var page = Page();

        await page.CheckNowCommand.ExecuteAsync(null);
        page.OpenReleaseCommand.Execute(null);

        Assert.Equal(1, _releases.Requests);
        Assert.Equal((Format(Strings.SettingsAboutUpdateAvailable, "0.5.0"), CheckedAt, true), (page.UpdateState, page.LastChecked, page.IsUpdateAvailable));
        Assert.Equal([FakeReleases.Page("v0.5.0")], _urls);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDate()
    {
        using var page = Page();

        await page.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal((Strings.SettingsAboutUpToDate, CheckedAt, null, false), (page.UpdateState, page.LastChecked, page.UpdateFailure, page.IsUpdateAvailable));
    }

    [Theory]
    [InlineData(UpdateCheckFailure.Unreachable, 0, "GitHub could not be reached.")]
    [InlineData(UpdateCheckFailure.TimedOut, 0, "GitHub did not answer within 10 seconds.")]
    [InlineData(UpdateCheckFailure.RateLimited, 403, "GitHub asks to wait before the next check.")]
    [InlineData(UpdateCheckFailure.Answered, 404, "GitHub answered 404.")]
    [InlineData(UpdateCheckFailure.BadAnswer, 0, "GitHub's answer names no release.")]
    public async Task AFailedCheckSaysWhyAndKeepsWhatWasKnown(UpdateCheckFailure failure, int status, string reason)
    {
        _releases.Next = new ReleaseLookup(null, failure, status);
        using var page = Page();

        await page.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal("The last check failed: " + reason, page.UpdateFailure);
        Assert.Equal(Strings.SettingsAboutNotChecked, page.UpdateState);
    }

    [Fact]
    public async Task WhileItChecksItSaysSoAndCheckNowWaits()
    {
        var answer = new TaskCompletionSource<ReleaseLookup>();
        _releases.Pending = answer.Task;
        using var page = Page();

        var check = page.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SettingsAboutChecking, page.UpdateState);
        Assert.False(page.CheckNowCommand.CanExecute(null));
        answer.SetResult(FakeReleases.Found("v0.4.0"));
        await check;
        Assert.Equal(Strings.SettingsAboutUpToDate, page.UpdateState);
    }

    // The daily check runs whether the page is open or not; an open page follows it.
    [Fact]
    public void AScheduledCheckUpdatesAnOpenPage()
    {
        _releases.Next = FakeReleases.Found("v0.5.0");
        using var page = Page();
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Checker().Enable(true);

        _timers.Fire();

        Assert.Equal(Format(Strings.SettingsAboutUpdateAvailable, "0.5.0"), page.UpdateState);
        Assert.Contains(nameof(AboutPage.IsUpdateAvailable), changed);
    }

    [Fact]
    public void AClosedPageNoLongerFollowsTheChecks()
    {
        _releases.Next = FakeReleases.Found("v0.5.0");
        var page = Page();
        page.Dispose();
        Checker().Enable(true);

        _timers.Fire();

        Assert.Equal(Strings.SettingsAboutNotChecked, page.UpdateState);
    }
}
