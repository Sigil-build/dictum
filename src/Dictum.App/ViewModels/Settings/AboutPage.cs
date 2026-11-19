using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.App.Updates;
using Dictum.Updates;
using Dictum.Updates.Install;
using Dictum.Core.Diagnostics;
using Dictum.Core.Docs;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the About page uses; fakes in tests.</summary>
/// <param name="Version">The version as the rail shows it, such as "0.4.0".</param>
/// <param name="System">The OS and the architecture, as <see cref="AboutPage.SystemText"/> words them.</param>
/// <param name="RuntimeId">The build's runtime identifier, such as "win-x64".</param>
/// <param name="Notices">THIRD-PARTY-NOTICES.txt next to the program; null where there is none, as in a build from source.</param>
/// <param name="Hardware">The shell's one GPU probe, which Settings → Models shares.</param>
/// <param name="Clipboard">The window's clipboard, for "Copy version and system info".</param>
/// <param name="OpenDocs">Documentation and Privacy.</param>
/// <param name="OpenUrl">GitHub's pages, and the notices file in the app that opens text files.</param>
/// <param name="Updates">The app's update check, whose state the page shows; null where there is none.</param>
/// <param name="Service">Downloads and installs an update; null where the page only links to the release.</param>
/// <param name="IsDictating">Whether the daemon records or processes now; then the install waits.</param>
/// <param name="WhenIdle">Runs its action once the daemon is idle: at once when it is, or when it next becomes so.</param>
/// <param name="Quit">The shell's Quit, once the installer waits for the app to exit.</param>
/// <param name="Report">What the update the last run started came to, read once on this start.</param>
/// <param name="Post">Runs on the UI thread; download progress comes from another. Null runs in place, as tests want.</param>
public sealed record AboutServices(
    string Version,
    string System,
    string RuntimeId,
    string? Notices,
    Func<Task<HardwareInfo>> Hardware,
    ITextClipboard Clipboard,
    OpenDocs OpenDocs,
    Action<Uri> OpenUrl,
    ILog Log,
    UpdateChecker? Updates = null,
    UpdateService? Service = null,
    Func<bool>? IsDictating = null,
    Action<Action>? WhenIdle = null,
    Action? Quit = null,
    UpdateReport? Report = null,
    Action<Action>? Post = null);

/// <summary>Which update card About Dictum shows (§4.2 of the self-update design).</summary>
public enum UpdateCard
{
    /// <summary>No newer version, or no way to act on one but its page.</summary>
    None,

    /// <summary>MSI, AppImage or tarball: Update now.</summary>
    CanUpdate,

    /// <summary>A copy that was not installed: the new version's page.</summary>
    Portable,

    /// <summary>The Arch package: the commands that build the new one.</summary>
    PackageManager,

    /// <summary>Downloading, verifying, waiting for a dictation, installing.</summary>
    Working,

    /// <summary>The update stopped; <see cref="AboutPage.UpdateProblem"/> says why.</summary>
    Failed,
}

/// <summary>
/// Settings → About Dictum, the rail's last page; the version at the rail's foot and the tray's "About Dictum" open it.
/// It names the version and the system, says what the update check found and checks on demand, links the
/// documentation and the project's pages, the license and the third-party notices, and copies the version and system
/// lines that a bug report asks for.
/// </summary>
public sealed partial class AboutPage : SettingsPage, IDisposable
{
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>Windows 11 reports itself as Windows 10.0; its builds start here.</summary>
    private const int Windows11Build = 22000;

    private readonly AboutServices _services;

    /// <summary>Stops the lookup of the offer's size when the page goes; an update that runs goes on.</summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The offer for this installation, with its package and size; null until a lookup found it.</summary>
    private UpdateOffer? _offer;

    private CancellationTokenSource? _cancel;
    private bool _working;

    public AboutPage(AboutServices services)
    {
        _services = services;
        UpdateReportText = services.Report switch
        {
            { Outcome: UpdateOutcome.DidNotFinish, Version: { } version } => UiText.Format(Strings.SettingsAboutUpdateDidNotFinish, version),
            { Outcome: UpdateOutcome.NeedsRestart, Version: { } version } => UiText.Format(Strings.SettingsAboutUpdateNeedsRestart, version),
            _ => null,
        };
        if (services.Updates is { } updates)
        {
            updates.Changed += ShowUpdates;
            ShowUpdates();
        }
    }

    /// <summary>The lookup of the offer's package and size that the page started last; done when there is none.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsUpdateNow), nameof(ShowsPortable), nameof(ShowsPackageManager), nameof(ShowsWork), nameof(ShowsProblem))]
    public partial UpdateCard Card { get; private set; }

    public bool ShowsUpdateNow => Card == UpdateCard.CanUpdate;

    public bool ShowsPortable => Card == UpdateCard.Portable;

    public bool ShowsPackageManager => Card == UpdateCard.PackageManager;

    public bool ShowsWork => Card == UpdateCard.Working;

    public bool ShowsProblem => Card == UpdateCard.Failed;

    /// <summary>The package's size, "29 MB" as Settings → Models words sizes; null while unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateNowLabel))]
    public partial string? UpdateSize { get; private set; }

    /// <summary>"Update now · 29 MB", or without the size while it is unknown.</summary>
    public string UpdateNowLabel => UpdateSize is { } size ? UiText.Format(Strings.SettingsAboutUpdateNowSize, size) : Strings.SettingsAboutUpdateNow;

    /// <summary>"Downloading… 12 MB of 29 MB", "Verifying…", "Waiting for the dictation to finish…", "Installing: Dictum will close now.".</summary>
    [ObservableProperty]
    public partial string? WorkText { get; private set; }

    /// <summary>The Arch package's three commands, one per line.</summary>
    [ObservableProperty]
    public partial string? PackageManagerCommands { get; private set; }

    [ObservableProperty]
    public partial string? CommandsCopyStatus { get; private set; }

    /// <summary>Why the update stopped, in the UI's language.</summary>
    [ObservableProperty]
    public partial string? UpdateProblem { get; private set; }

    /// <summary>A failed download: Try again resumes it.</summary>
    [ObservableProperty]
    public partial bool CanRetry { get; private set; }

    /// <summary>"The update to 0.7.1 did not finish." or "Restart Windows to finish the update to 0.7.1."; null after a clean start.</summary>
    public string? UpdateReportText { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    public partial bool CanCancel { get; private set; }

    public override string Title => Strings.SettingsAbout;

    /// <summary>The app checks for updates, so the page has an Updates part.</summary>
    public bool HasUpdates => _services.Updates is not null;

    /// <summary>"Dictum is up to date.", "Version 0.5.0 is available.", "Checking for updates…" or "Not checked yet.".</summary>
    [ObservableProperty]
    public partial string? UpdateState { get; private set; }

    /// <summary>"Last checked 22/10/2026 09:30", in the UI's culture and the computer's time zone; null before any check.</summary>
    [ObservableProperty]
    public partial string? LastChecked { get; private set; }

    /// <summary>Why the last check of this run failed; null after one that worked.</summary>
    [ObservableProperty]
    public partial string? UpdateFailure { get; private set; }

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; private set; }

    public void Dispose()
    {
        if (_services.Updates is { } updates)
        {
            updates.Changed -= ShowUpdates;
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    public override string DocsAnchor => DocsAnchors.Settings.About;

    /// <summary>"Dictum 0.4.0", as the rail's foot shows it.</summary>
    public string Version => UiText.Format(Strings.SettingsVersion, _services.Version);

    public string System => _services.System;

    /// <summary>What the last copy did: copied, or why not.</summary>
    [ObservableProperty]
    public partial string? CopyStatus { get; private set; }

    /// <summary>The notices file in <paramref name="directory"/>, the program's folder, or null when it is not there.</summary>
    public static string? Notices(string directory)
    {
        var file = Path.Combine(directory, NoticesFileName);
        return File.Exists(file) ? file : null;
    }

    /// <summary>"Windows 11 (10.0.26200), x64"; on Linux the distribution and the session, "Ubuntu 24.04.1 LTS (Wayland, KDE), x64".</summary>
    /// <param name="osDescription">What .NET calls the OS; on Windows the release is named from the build instead.</param>
    /// <param name="session">The Linux session, as Settings → System names it; null elsewhere.</param>
    public static string SystemText(bool windows, Version osVersion, string osDescription, string? session, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(osVersion);
        var os = windows
            ? $"Windows {(osVersion.Build >= Windows11Build ? 11 : 10)} ({osVersion.Major}.{osVersion.Minor}.{osVersion.Build})"
            : session is null ? osDescription : $"{osDescription} ({session})";
        return $"{os}, {architecture.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// This computer's <see cref="SystemText"/>. On Linux .NET describes the kernel, so the distribution's own name from
    /// os-release stands in for it where there is one.
    /// </summary>
    public static string ThisSystem(DesktopEnvironment desktop)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var description = desktop.IsLinux ? LinuxDistribution() ?? RuntimeInformation.OSDescription : RuntimeInformation.OSDescription;
        return SystemText(desktop.IsWindows, Environment.OSVersion.Version, description, desktop.Session?.ToString(), RuntimeInformation.OSArchitecture);
    }

    /// <summary>The <c>PRETTY_NAME</c> of an os-release file's lines, such as "Ubuntu 24.04.1 LTS"; null when it has none.</summary>
    public static string? Distribution(IEnumerable<string> osRelease)
    {
        ArgumentNullException.ThrowIfNull(osRelease);
        const string Key = "PRETTY_NAME=";
        var line = osRelease.FirstOrDefault(l => l.StartsWith(Key, StringComparison.Ordinal));
        return line?[Key.Length..].Trim().Trim('"', '\'') is { Length: > 0 } name ? name : null;
    }

    /// <summary>os-release's name for this distribution, from where systemd looks for the file.</summary>
    private static string? LinuxDistribution()
    {
        foreach (var file in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                if (File.Exists(file))
                {
                    return Distribution(File.ReadLines(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable file: the kernel's description stays.
            }
        }

        return null;
    }

    /// <summary>Asks GitHub now, whether the daily check is on or not; disabled while a check or an update runs.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() => _services.Updates?.CheckNowAsync() ?? Task.CompletedTask;

    private bool CanCheckNow() => !_working;

    private bool CanUpdateNow() => !_working && _services.Service is not null && Card is UpdateCard.CanUpdate or UpdateCard.Failed;

    /// <summary>
    /// One click is the consent: download, verify, wait for a dictation to end, hand the package to the installer and
    /// quit, so the installer can replace this version and start the new one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateNow))]
    private async Task UpdateNowAsync()
    {
        if (_working || _services.Service is not { } service)
        {
            return;
        }

        _working = true;
        _cancel = new CancellationTokenSource();
        (UpdateProblem, CanRetry, WorkText, Card) = (null, false, null, UpdateCard.Working);
        NotifyUpdateCommands();
        try
        {
            if (_offer is not { Package: not null })
            {
                var (offer, lookup) = await service.FindAsync(_cancel.Token);
                _offer = offer ?? throw new UpdateException(Dictum.Updates.UpdateFailure.Download, lookup.Detail ?? lookup.Failure.ToString());
            }

            CanCancel = true;
            var update = await service.DownloadAsync(_offer, new WorkReporter(this), _cancel.Token);
            CanCancel = false;
            if (_services.IsDictating?.Invoke() == true && _services.WhenIdle is { } whenIdle)
            {
                WorkText = Strings.SettingsAboutWaitingForDictation;
                var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                whenIdle(() => idle.TrySetResult());
                await idle.Task;
            }

            WorkText = Strings.SettingsAboutInstalling;
            service.Install(update, RestartTarget.DesktopApp);
            _services.Log.Info($"Installing Dictum {update.Offer.Version}; the app quits so the installer can replace it");
            _services.Quit?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _working = false;
            WorkText = null;
            Card = UpdateCard.None;
            ShowCard();
        }
        catch (UpdateException ex)
        {
            _services.Log.Warn($"The update stopped ({ex.Failure}): {ex.Message}");
            _working = false;
            WorkText = null;
            UpdateProblem = ex.Failure switch
            {
                Dictum.Updates.UpdateFailure.Download => UiText.Format(Strings.SettingsAboutUpdateDownloadFailed, ex.Message.TrimEnd('.')),
                Dictum.Updates.UpdateFailure.NotVerified => Strings.SettingsAboutUpdateNotVerified,
                Dictum.Updates.UpdateFailure.NoPackage => Strings.SettingsAboutUpdateNoPackage,
                Dictum.Updates.UpdateFailure.NoRoom => Strings.SettingsAboutUpdateNoRoom,
                _ => Strings.SettingsAboutUpdateCannotInstall,
            };
            CanRetry = ex.Failure == Dictum.Updates.UpdateFailure.Download;
            Card = UpdateCard.Failed;
        }
        finally
        {
            CanCancel = false;
            _cancel.Dispose();
            _cancel = null;
            NotifyUpdateCommands();
        }
    }

    /// <summary>Possible while downloading and verifying; not once the install waits or has started.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelUpdate() => _cancel?.Cancel();

    [RelayCommand]
    private async Task CopyCommandsAsync()
    {
        if (PackageManagerCommands is not { } commands)
        {
            return;
        }

        try
        {
            await _services.Clipboard.SetTextAsync(commands);
            CommandsCopyStatus = Strings.SettingsAboutCommandsCopied;
        }
        catch (Exception ex)
        {
            // A busy clipboard throws; a command that throws would take dictum-gui down.
            CommandsCopyStatus = UiText.Format(Strings.SettingsAboutCopyFailed, ex.Message);
            _services.Log.Warn($"Update commands not copied: {ex.Message}");
        }
    }

    /// <summary>The unfinished update left a log to open.</summary>
    public bool HasUpdateLog => _services.Report is { Outcome: UpdateOutcome.DidNotFinish, Log: not null };

    private bool CanOpenUpdateLog() => HasUpdateLog;

    [RelayCommand(CanExecute = nameof(CanOpenUpdateLog))]
    private void OpenUpdateLog()
    {
        if (_services.Report?.Log is { } log)
        {
            _services.OpenUrl(new Uri(log));
        }
    }

    private void NotifyUpdateCommands()
    {
        UpdateNowCommand.NotifyCanExecuteChanged();
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The card for what the check found and how Dictum was installed; a failure stays until the next check.</summary>
    private void ShowCard()
    {
        if (_working)
        {
            return;
        }

        var available = _services.Updates?.Status.Available;
        Card = available is null || _services.Service is not { } service ? UpdateCard.None : service.Installation.Kind switch
        {
            InstallKind.Msi or InstallKind.AppImage or InstallKind.Tarball => UpdateCard.CanUpdate,
            InstallKind.PackageManager => UpdateCard.PackageManager,
            _ => UpdateCard.Portable,
        };
        PackageManagerCommands = Card == UpdateCard.PackageManager && ReleaseVersion.Parse(available!.Version) is { } version
            ? ManualUpdate.PackageManagerCommands(version)
            : null;
        if (Card == UpdateCard.CanUpdate && _offer?.Version.ToString() != available!.Version)
        {
            Loading = LoadOfferAsync();
        }

        NotifyUpdateCommands();
    }

    /// <summary>The release's package and its size for the button; a failed lookup leaves the button without one.</summary>
    private async Task LoadOfferAsync()
    {
        try
        {
            var (offer, _) = await _services.Service!.FindAsync(_stop.Token);
            _offer = offer;
            UpdateSize = offer?.Package is { } package ? Units.Size(package.Size) : null;
        }
        catch (OperationCanceledException)
        {
            // The page is gone.
        }
        catch (Exception ex)
        {
            // The source says why instead of throwing; anything else must not take dictum-gui down.
            _services.Log.Info($"The update's size is unknown: {ex.Message}");
        }
    }

    private void ShowWork(UpdateProgress progress)
    {
        var text = progress.Stage switch
        {
            UpdateStage.Downloading => UiText.Format(Strings.SettingsAboutDownloading, Units.Size(progress.Bytes), Units.Size(progress.Total)),
            UpdateStage.Verifying => Strings.SettingsAboutVerifying,
            _ => Strings.SettingsAboutInstalling,
        };
        if (_services.Post is { } post)
        {
            post(() => WorkText = text);
        }
        else
        {
            WorkText = text;
        }
    }

    /// <summary>The download's progress, which comes from the downloading thread.</summary>
    private sealed class WorkReporter(AboutPage page) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => page.ShowWork(value);
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (_services.Updates?.Status.Available is { } update)
        {
            _services.OpenUrl(update.Page);
        }
    }

    private void ShowUpdates()
    {
        var status = _services.Updates!.Status;
        UpdateState = status switch
        {
            { Checking: true } => Strings.SettingsAboutChecking,
            { Available: { } update } => UiText.Format(Strings.SettingsAboutUpdateAvailable, update.Version),
            { LastChecked: not null } => Strings.SettingsAboutUpToDate,
            _ => Strings.SettingsAboutNotChecked,
        };
        LastChecked = status.LastChecked is { } at
            ? UiText.Format(Strings.SettingsAboutLastChecked, at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : null;
        UpdateFailure = Reason(status.Failure, status.StatusCode) is { } reason ? UiText.Format(Strings.SettingsAboutCheckFailed, reason) : null;
        IsUpdateAvailable = status.Available is not null;
        ShowCard();
    }

    private static string? Reason(UpdateCheckFailure failure, int statusCode) => failure switch
    {
        UpdateCheckFailure.Unreachable => Strings.SettingsAboutCheckUnreachable,
        UpdateCheckFailure.TimedOut => Strings.SettingsAboutCheckTimedOut,
        UpdateCheckFailure.RateLimited => Strings.SettingsAboutCheckRateLimited,
        UpdateCheckFailure.Answered => UiText.Format(Strings.SettingsAboutCheckAnswered, statusCode),
        UpdateCheckFailure.BadAnswer => Strings.SettingsAboutCheckBadAnswer,
        _ => null,
    };

    [RelayCommand]
    private void Documentation() => _services.OpenDocs(DocsPage.Home);

    [RelayCommand]
    private void Privacy() => _services.OpenDocs(DocsPage.Privacy);

    [RelayCommand]
    private void GitHub() => _services.OpenUrl(ProjectLinks.Repository);

    [RelayCommand]
    private void Releases() => _services.OpenUrl(ProjectLinks.Releases);

    [RelayCommand]
    private void ReportProblem() => _services.OpenUrl(ProjectLinks.NewIssue);

    [RelayCommand]
    private void License() => _services.OpenUrl(ProjectLinks.License);

    /// <summary>The notices of exactly this build when they are next to it, as in every package; GitHub's otherwise.</summary>
    [RelayCommand]
    private void Notices() => _services.OpenUrl(_services.Notices is { } file ? new Uri(file) : ProjectLinks.Notices);

    [RelayCommand]
    private async Task CopyInfoAsync()
    {
        var text = await InfoAsync();
        try
        {
            await _services.Clipboard.SetTextAsync(text);
            CopyStatus = Strings.SettingsAboutCopied;
        }
        catch (Exception ex)
        {
            // A busy clipboard throws; a command that throws would take dictum-gui down.
            CopyStatus = UiText.Format(Strings.SettingsAboutCopyFailed, ex.Message);
            _services.Log.Warn($"Version and system info not copied: {ex.Message}");
        }
    }

    /// <summary>
    /// "Dictum 0.4.0 · Windows 11 (10.0.26200), x64 · win-x64", then the GPU Whisper can use and the .NET runtime. In
    /// English whatever the UI's language, as the issue tracker is.
    /// </summary>
    private async Task<string> InfoAsync()
    {
        string? gpu;
        try
        {
            gpu = "GPU: " + (HardwareText.Gpu(await _services.Hardware()) ?? "none");
        }
        catch (Exception ex)
        {
            // A broken Vulkan driver must not stop the copy; the line leaves the GPU out.
            _services.Log.Warn($"GPU probe failed: {ex.Message}", ex);
            gpu = null;
        }

        var runtime = RuntimeInformation.FrameworkDescription;
        return $"Dictum {_services.Version} · {_services.System} · {_services.RuntimeId}\n{(gpu is null ? runtime : $"{gpu} · {runtime}")}";
    }
}
