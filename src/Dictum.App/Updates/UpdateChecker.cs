using Dictum.App.Settings;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Updates;

/// <summary>A newer Dictum than this one, and its release page.</summary>
public sealed record AvailableUpdate(string Version, Uri Page);

/// <summary>What the About page and the tray show of the update check.</summary>
/// <param name="Available">A newer release; null when there is none or no check has found one.</param>
/// <param name="LastChecked">When a check last got an answer, from this run or an earlier one.</param>
/// <param name="Failure">Why the last check of this run found nothing; <see cref="UpdateCheckFailure.None"/> after one that worked.</param>
/// <param name="StatusCode">GitHub's status when <paramref name="Failure"/> is <see cref="UpdateCheckFailure.Answered"/>.</param>
public sealed record UpdateStatus(AvailableUpdate? Available, DateTimeOffset? LastChecked, bool Checking, UpdateCheckFailure Failure, int StatusCode = 0);

/// <summary>
/// The desktop app's update check. While <c>general.checkForUpdates</c> is on, it asks GitHub a minute after
/// <see cref="Enable"/>, then every 24 hours. What a check found survives a restart in <c>update-check.json</c>, and so
/// does the last version announced: <see cref="Announced"/> tells of each new version once. A failed check is one Info
/// line in the log and nothing else; the next interval tries again. Runs on the UI thread, as its timers do.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IReleaseSource _releases;
    private readonly UpdateCheckFile _file;
    private readonly ITimers _timers;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly ReleaseVersion? _current;
    private readonly CancellationTokenSource _stop = new();
    private UpdateCheckState _state;
    private IDisposable? _first;
    private IDisposable? _daily;
    private Task? _running;

    /// <param name="appVersion">This app's informational version; one that does not parse is offered nothing.</param>
    public UpdateChecker(IReleaseSource releases, UpdateCheckFile file, ITimers timers, TimeProvider time, string appVersion, ILog log)
    {
        _releases = releases;
        _file = file;
        _timers = timers;
        _time = time;
        _log = log;
        _current = ReleaseVersion.OfApp(appVersion);
        _state = file.Load();
        Status = new UpdateStatus(Newer(_state.Latest, _state.Page), _state.LastChecked, false, UpdateCheckFailure.None);
    }

    public UpdateStatus Status { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary><see cref="Status"/> changed.</summary>
    public event Action? Changed;

    /// <summary>A scheduled check found a version the user has not been told about yet.</summary>
    public event Action<AvailableUpdate>? Announced;

    /// <summary>On: the first check a minute from now, then one a day. Off: the timers stop at once.</summary>
    public void Enable(bool enabled)
    {
        if (enabled == Enabled)
        {
            return;
        }

        Enabled = enabled;
        StopTimers();
        if (enabled)
        {
            _first = _timers.Once(FirstCheck, () =>
            {
                _first = null;
                _daily = _timers.Every(Interval, () => _ = CheckAsync(manual: false));
                _ = CheckAsync(manual: false);
            });
        }
    }

    /// <summary>
    /// About's <b>Check now</b>, whether the setting is on or not. The page shows what it finds, so the version found
    /// counts as told and no notification follows. Joins a check already running.
    /// </summary>
    public Task CheckNowAsync() => CheckAsync(manual: true);

    public void Dispose()
    {
        StopTimers();
        _stop.Cancel();
        _stop.Dispose();
    }

    private void StopTimers()
    {
        _first?.Dispose();
        _daily?.Dispose();
        _first = null;
        _daily = null;
    }

    private Task CheckAsync(bool manual)
    {
        if (_running is { IsCompleted: false } running)
        {
            return running;
        }

        return _running = RunAsync(manual);
    }

    private async Task RunAsync(bool manual)
    {
        ReleaseLookup lookup;
        Show(Status with { Checking = true });
        try
        {
            lookup = await _releases.LatestAsync(_stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The app is quitting.
            return;
        }
        catch (Exception ex)
        {
            // The source says why instead of throwing; anything else must not take dictum-gui down from a timer.
            lookup = new ReleaseLookup(null, UpdateCheckFailure.Unreachable, Detail: ex.Message);
        }

        if (lookup.Release is not { } release)
        {
            _log.Info($"Update check failed: {lookup.Detail ?? lookup.Failure.ToString()}; the next one tries again");
            Show(Status with { Checking = false, Failure = lookup.Failure, StatusCode = lookup.StatusCode });
            return;
        }

        var latest = ReleaseVersion.Parse(release.Tag);
        var available = Newer(latest?.ToString(), release.Page.AbsoluteUri);
        var tell = available is not null && !Told(latest!.Value);
        _state = _state with
        {
            LastChecked = _time.GetUtcNow(),
            Latest = latest?.ToString(),
            Page = release.Page.AbsoluteUri,
            Notified = tell ? available!.Version : _state.Notified,
        };
        _file.Save(_state);
        Show(new UpdateStatus(available, _state.LastChecked, false, UpdateCheckFailure.None));
        if (tell && !manual)
        {
            Announced?.Invoke(available!);
        }
    }

    /// <summary>The user was told of this version, or of a later one.</summary>
    private bool Told(ReleaseVersion version) => ReleaseVersion.Parse(_state.Notified) is { } told && told >= version;

    /// <summary>
    /// The release, when it is newer than this app and its page is Dictum's release page on GitHub: the page may come
    /// from <c>update-check.json</c>, and a click opens it.
    /// </summary>
    private AvailableUpdate? Newer(string? version, string? page) =>
        _current is { } current && ReleaseVersion.Parse(version) is { } latest && latest > current
        && GitHubReleases.IsReleasePage(page, out var uri)
            ? new AvailableUpdate(latest.ToString(), uri)
            : null;

    private void Show(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
