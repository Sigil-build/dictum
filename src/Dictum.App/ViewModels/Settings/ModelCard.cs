using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Settings;

/// <summary>A profile whose speech provider is a local Whisper, as the Models page needs it.</summary>
/// <param name="Provider">The profile's speech provider id.</param>
/// <param name="Model">That provider's model: a file name in the models folder, or an absolute path.</param>
public sealed record LocalProfile(string Id, string Name, string Provider, string Model);

/// <summary>What the Models page knows about one model when it refreshes.</summary>
/// <param name="Download">The queue's entry for the model, or null.</param>
/// <param name="Progress">"949 MB of 1.53 GB · 18 MB/s · 32 s left" while the file is in the queue; otherwise null.</param>
/// <param name="ForGpu">The recommendation is the GPU one.</param>
/// <param name="UsedBy">The profiles whose provider uses this file in the edited copy.</param>
/// <param name="UsedOnDisk">The same in the saved config, which the daemon runs until the next save.</param>
/// <param name="Profiles">Every profile with a local Whisper provider, for "Use in profile…".</param>
/// <param name="CanEdit">The config was read, so "Used by" can be trusted.</param>
public sealed record ModelCardState(
    bool Installed,
    QueuedDownload? Download,
    string? Progress,
    SpeedEstimate? Estimate,
    bool Recommended,
    bool ForGpu,
    IReadOnlyList<LocalProfile> UsedBy,
    IReadOnlyList<LocalProfile> UsedOnDisk,
    IReadOnlyList<LocalProfile> Profiles,
    bool CanEdit);

/// <summary>
/// One model on Settings → Models: its badges, the size · licence · source · languages line,
/// the speed estimate and "Used by", and the actions for its state:
/// <list type="bullet">
/// <item>installed: Use in profile…, Verify, Measure speed, and Delete, which is off while a profile uses the file;</item>
/// <item>waiting or downloading: Pause and Cancel; paused: Resume and Cancel; failed: Try again and Cancel;</item>
/// <item>not installed: Download, or "Download anyway" when it would be too slow here.</item>
/// </list>
/// The page refreshes it in place.
/// </summary>
public sealed partial class ModelCard : ObservableObject
{
    private readonly ModelsPage _page;
    private ModelCardState? _state;

    internal ModelCard(CatalogEntry entry, ModelsPage page)
    {
        Entry = entry;
        _page = page;
        Name = ModelText.Name(entry);
        Note = ModelText.Note(entry);
        Meta = ModelText.Meta(entry);
        DeleteText = string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsDelete, Units.Size(entry.Size));
    }

    public CatalogEntry Entry { get; }

    public string Name { get; }

    public string Note { get; }

    public string Meta { get; }

    public string DeleteText { get; }

    [ObservableProperty]
    public partial string? Recommendation { get; private set; }

    [ObservableProperty]
    public partial string StateText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsInstalled { get; private set; }

    /// <summary>Waiting or downloading: Pause and Cancel.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    [ObservableProperty]
    public partial bool CanCancel { get; private set; }

    [ObservableProperty]
    public partial bool ShowDownload { get; private set; }

    [ObservableProperty]
    public partial bool ShowDownloadAnyway { get; private set; }

    [ObservableProperty]
    public partial string DownloadText { get; private set; } = "";

    [ObservableProperty]
    public partial string? SpeedText { get; private set; }

    [ObservableProperty]
    public partial string? SpeedBadge { get; private set; }

    [ObservableProperty]
    public partial bool IsTooSlow { get; private set; }

    [ObservableProperty]
    public partial string UsedByText { get; private set; } = "";

    /// <summary>Why Delete is off: the profiles that use the file.</summary>
    [ObservableProperty]
    public partial string? DeleteNote { get; private set; }

    [ObservableProperty]
    public partial bool CanDelete { get; private set; }

    [ObservableProperty]
    public partial bool CanVerify { get; private set; }

    /// <summary>A profile uses the file and no dictation has timed it yet; a dictation's timing outranks a measurement.</summary>
    [ObservableProperty]
    public partial bool ShowMeasure { get; private set; }

    [ObservableProperty]
    public partial bool CanMeasure { get; private set; }

    /// <summary>0–100.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    [ObservableProperty]
    public partial string? DownloadError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> UseInChoices { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasUseIn { get; private set; }

    /// <summary>The "Use in profile…" pick; the view clears the dropdown after it.</summary>
    [ObservableProperty]
    public partial Option<string>? UseIn { get; set; }

    /// <summary>Verifying or measuring.</summary>
    [ObservableProperty]
    public partial bool Busy { get; internal set; }

    /// <summary>What the last action on this card could not do.</summary>
    [ObservableProperty]
    public partial string? Error { get; internal set; }

    /// <summary>The state or result of Verify and Measure.</summary>
    [ObservableProperty]
    public partial string? CheckText { get; internal set; }

    internal void Update(ModelCardState state)
    {
        _state = state;
        Apply();
    }

    partial void OnBusyChanged(bool value) => Apply();

    partial void OnUseInChanged(Option<string>? value)
    {
        if (value is not null)
        {
            _page.UseIn(this, value.Value);
        }
    }

    [RelayCommand]
    private void Download() => _page.Download(this);

    [RelayCommand]
    private void Pause() => _page.Pause(this);

    [RelayCommand]
    private void Resume() => _page.Resume(this);

    [RelayCommand]
    private void Cancel() => _page.Cancel(this);

    [RelayCommand]
    private void Delete() => _page.Delete(this);

    [RelayCommand]
    private Task VerifyAsync() => _page.VerifyAsync(this);

    [RelayCommand]
    private Task MeasureAsync() => _page.MeasureAsync(this);

    private void Apply()
    {
        if (_state is not { } state)
        {
            return;
        }

        // A finished download is just an installed file.
        var download = state.Download is { State: not DownloadState.Done } item ? item : null;
        // Used in the copy or on disk: an edit not saved yet, or one that cannot be saved, never frees the file.
        var users = state.UsedBy.Concat(state.UsedOnDisk).DistinctBy(p => p.Id).Select(p => p.Name).ToList();
        var used = users.Count > 0;
        IsInstalled = state.Installed && download is null;
        IsActive = download?.State is DownloadState.Queued or DownloadState.Downloading;
        IsPaused = download?.State == DownloadState.Paused;
        IsFailed = download?.State == DownloadState.Failed;
        CanCancel = download is not null;
        StateText = download?.State switch
        {
            DownloadState.Downloading => Strings.SettingsModelsDownloading,
            DownloadState.Queued => Strings.SettingsModelsQueued,
            DownloadState.Paused => Strings.SettingsModelsPaused,
            DownloadState.Failed => Strings.SettingsModelsFailed,
            _ => state.Installed ? Strings.SettingsModelsInstalled : Strings.SettingsModelsNotInstalled,
        };
        Recommendation = !state.Recommended ? null
            : state.ForGpu ? Strings.SettingsModelsRecommendedGpu : Strings.SettingsModelsRecommendedCpu;
        SpeedText = state.Estimate is { } estimate ? ModelText.Speed(estimate) : null;
        SpeedBadge = state.Estimate?.Class switch
        {
            SpeedClass.Fast => Strings.SettingsModelsFast,
            SpeedClass.Usable => Strings.SettingsModelsUsable,
            SpeedClass.TooSlow => Strings.SettingsModelsTooSlow,
            _ => null,
        };
        IsTooSlow = state.Estimate?.Class == SpeedClass.TooSlow;
        var offered = (!state.Installed && download is null) || IsFailed;
        ShowDownload = offered && !IsTooSlow;
        ShowDownloadAnyway = offered && IsTooSlow;
        DownloadText = string.Format(CultureInfo.CurrentCulture,
            IsFailed ? Strings.SettingsModelsRetry : IsTooSlow ? Strings.SettingsModelsDownloadAnyway : Strings.SettingsModelsDownload,
            Units.Size(Entry.Size));
        var names = string.Join(", ", users);
        UsedByText = used ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersUsedBy, names) : Strings.SettingsModelsUnused;
        DeleteNote = IsInstalled && used ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsDeleteRefused, names) : null;
        CanDelete = IsInstalled && !used && state.CanEdit && !Busy;
        CanVerify = IsInstalled && !Busy;
        ShowMeasure = IsInstalled && used && state.CanEdit && state.Estimate?.Source != SpeedSource.Dictations;
        CanMeasure = ShowMeasure && !Busy;
        Progress = download is null ? 0 : 100.0 * Math.Min(download.Bytes, Entry.Size) / Math.Max(1, Entry.Size);
        ProgressText = IsFailed ? null : state.Progress;
        DownloadError = IsFailed ? download?.Error : null;
        List<Option<string>> choices = IsInstalled && state.CanEdit ? Choices(state) : [];
        if (!choices.SequenceEqual(UseInChoices))
        {
            UseInChoices = choices;
        }

        HasUseIn = UseInChoices.Count > 0;
    }

    /// <summary>The local-Whisper profiles not on this file yet; a provider two profiles share says so ("Work (also Home)").</summary>
    private static List<Option<string>> Choices(ModelCardState state) =>
    [
        .. state.Profiles.Where(p => !state.UsedBy.Contains(p)).Select(p =>
        {
            var others = state.Profiles.Where(o => o.Provider == p.Provider && o.Id != p.Id).Select(o => o.Name).ToList();
            return new Option<string>(p.Id, others.Count == 0
                ? p.Name
                : string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsUseInShared, p.Name, string.Join(", ", others)));
        }),
    ];
}
