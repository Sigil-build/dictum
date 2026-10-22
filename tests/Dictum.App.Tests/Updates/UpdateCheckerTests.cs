using Dictum.App.Tests.Settings;
using Dictum.App.Updates;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Tests.Updates;

/// <summary>The schedule and the state of the update check, on manual timers, a manual clock and a fake GitHub.</summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly Uri Page050 = new("https://github.com/Sigil-build/dictum/releases/tag/v0.5.0");
    private static readonly Uri Page060 = new("https://github.com/Sigil-build/dictum/releases/tag/v0.6.0");

    private readonly TempDirectory _dir = new();
    private readonly ManualTimers _timers = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeReleases _releases = new();
    private readonly ListLog _log = new();
    private readonly List<AvailableUpdate> _announced = [];

    public void Dispose() => _dir.Dispose();

    private string File => _dir.File("update-check.json");

    private UpdateChecker Checker(string version = "0.4.0")
    {
        var checker = new UpdateChecker(_releases, new UpdateCheckFile(File, _log), _timers, _clock, version, _log);
        checker.Announced += _announced.Add;
        return checker;
    }

    private static ReleaseLookup Found(string tag, Uri page) => new(new LatestRelease(tag, page));

    [Fact]
    public void TheFirstCheckIsAMinuteAfterStartThenEveryDay()
    {
        using var checker = Checker();

        checker.Enable(true);
        Assert.Equal(TimeSpan.FromMinutes(1), _timers.LastDelay);
        Assert.Equal(0, _releases.Requests);

        _timers.Fire();
        Assert.Equal(1, _releases.Requests);
        Assert.Equal(TimeSpan.FromHours(24), _timers.LastDelay);

        _timers.Fire();
        Assert.Equal(2, _releases.Requests);
    }

    [Fact]
    public void NothingIsCheckedUntilItIsTurnedOn()
    {
        using var checker = Checker();

        _timers.Fire();

        Assert.Equal((0, 0), (_releases.Requests, _timers.Pending));
    }

    [Fact]
    public void TurningItOffStopsTheTimersAtOnce()
    {
        using var checker = Checker();
        checker.Enable(true);
        _timers.Fire();

        checker.Enable(false);
        _timers.Fire();

        Assert.Equal((1, 0), (_releases.Requests, _timers.Pending));
    }

    [Fact]
    public void TurningItOnAgainWaitsAMinuteAgain()
    {
        using var checker = Checker();
        checker.Enable(true);
        checker.Enable(false);

        checker.Enable(true);
        checker.Enable(true);

        Assert.Equal((1, TimeSpan.FromMinutes(1)), (_timers.Pending, _timers.LastDelay));
    }

    [Fact]
    public void ANewerVersionIsAvailableAndAnnouncedOnce()
    {
        _releases.Next = Found("v0.5.0", Page050);
        using var checker = Checker();
        checker.Enable(true);

        _timers.Fire();
        _timers.Fire();

        Assert.Equal(new AvailableUpdate("0.5.0", Page050), checker.Status.Available);
        Assert.Equal(_clock.GetUtcNow(), checker.Status.LastChecked);
        Assert.Equal([new AvailableUpdate("0.5.0", Page050)], _announced);
        Assert.Equal(2, _releases.Requests);
    }

    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("v0.3.2")]
    [InlineData("v0.5.0-rc.1")]
    [InlineData("latest")]
    public void TheSameAnOlderOrAnUnreadableVersionIsNoUpdate(string tag)
    {
        _releases.Next = Found(tag, Page050);
        using var checker = Checker();
        checker.Enable(true);

        _timers.Fire();

        Assert.Equal(new UpdateStatus(null, _clock.GetUtcNow(), Checking: false, UpdateCheckFailure.None), checker.Status);
        Assert.Empty(_announced);
    }

    // A restart reads the last check, so the tray offers the version at once and does not announce it again.
    [Fact]
    public void TheStateSurvivesARestart()
    {
        _releases.Next = Found("v0.5.0", Page050);
        var checkedAt = _clock.GetUtcNow();
        using (var first = Checker())
        {
            first.Enable(true);
            _timers.Fire();
        }

        _clock.Advance(TimeSpan.FromHours(3));
        using var second = Checker();
        Assert.Equal(new UpdateStatus(new AvailableUpdate("0.5.0", Page050), checkedAt, false, UpdateCheckFailure.None), second.Status);

        second.Enable(true);
        _timers.Fire();
        Assert.Single(_announced);

        _releases.Next = Found("v0.6.0", Page060);
        _timers.Fire();
        Assert.Equal([new AvailableUpdate("0.5.0", Page050), new AvailableUpdate("0.6.0", Page060)], _announced);
    }

    [Fact]
    public void AnUpdateInstalledSinceIsNotOffered()
    {
        _releases.Next = Found("v0.5.0", Page050);
        using (var old = Checker("0.4.0"))
        {
            old.Enable(true);
            _timers.Fire();
        }

        using var updated = Checker("0.5.0");

        Assert.Null(updated.Status.Available);
    }

    [Theory]
    [InlineData(UpdateCheckFailure.Unreachable)]
    [InlineData(UpdateCheckFailure.TimedOut)]
    [InlineData(UpdateCheckFailure.RateLimited)]
    [InlineData(UpdateCheckFailure.Answered)]
    [InlineData(UpdateCheckFailure.BadAnswer)]
    public void AFailedCheckIsSilentAndRetriedAtTheNextInterval(UpdateCheckFailure failure)
    {
        _releases.Next = new ReleaseLookup(null, failure, 503, "why");
        using var checker = Checker();
        checker.Enable(true);

        _timers.Fire();

        Assert.Equal(new UpdateStatus(null, null, false, failure, 503), checker.Status);
        Assert.Empty(_announced);
        Assert.False(System.IO.File.Exists(File));
        var line = Assert.Single(_log.Lines);
        Assert.Equal(LogLevel.Info, line.Level);
        Assert.StartsWith("Update check failed: ", line.Message, StringComparison.Ordinal);

        _releases.Next = Found("v0.5.0", Page050);
        _timers.Fire();
        Assert.Equal(new UpdateStatus(new AvailableUpdate("0.5.0", Page050), _clock.GetUtcNow(), false, UpdateCheckFailure.None), checker.Status);
    }

    // A source that throws all the same must not take dictum-gui down from a timer.
    [Fact]
    public void ASourceThatThrowsIsAFailedCheck()
    {
        _releases.Throws = new InvalidOperationException("broken");
        using var checker = Checker();
        checker.Enable(true);

        _timers.Fire();

        Assert.Equal(UpdateCheckFailure.Unreachable, checker.Status.Failure);
        Assert.True(_log.Has("broken"));
    }

    // Check now shows its answer on the page that asked; the version counts as told, so no notification follows.
    [Fact]
    public async Task CheckNowChecksAtOnceAndAnnouncesNothing()
    {
        _releases.Next = Found("v0.5.0", Page050);
        using var checker = Checker();

        await checker.CheckNowAsync();
        checker.Enable(true);
        _timers.Fire();

        Assert.Equal(new AvailableUpdate("0.5.0", Page050), checker.Status.Available);
        Assert.Empty(_announced);
        Assert.Equal(2, _releases.Requests);
    }

    [Fact]
    public async Task ChecksDoNotOverlap()
    {
        var answer = new TaskCompletionSource<ReleaseLookup>(TaskCreationOptions.RunContinuationsAsynchronously);
        _releases.Pending = answer.Task;
        using var checker = Checker();
        var changes = new List<bool>();
        checker.Changed += () => changes.Add(checker.Status.Checking);
        checker.Enable(true);

        _timers.Fire();
        var now = checker.CheckNowAsync();
        Assert.True(checker.Status.Checking);
        answer.SetResult(Found("v0.4.0", Page050));
        await now;

        Assert.Equal(1, _releases.Requests);
        Assert.Equal([true, false], changes);
        Assert.False(checker.Status.Checking);
    }

    [Fact]
    public void AnAppVersionThatDoesNotParseOffersNothing()
    {
        _releases.Next = Found("v9.9.9", Page050);
        using var checker = Checker("?");
        checker.Enable(true);

        _timers.Fire();

        Assert.Null(checker.Status.Available);
        Assert.Empty(_announced);
    }

    [Fact]
    public void DisposeStopsTheTimers()
    {
        var checker = Checker();
        checker.Enable(true);

        checker.Dispose();

        Assert.Equal(0, _timers.Pending);
    }
}
