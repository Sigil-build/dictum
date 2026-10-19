using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A file on step 7: its progress, and why it stopped when it failed; Try again hands it back to the step.</summary>
public sealed partial class FileProgress(CatalogEntry entry, Action<FileProgress> retry) : ObservableObject
{
    public CatalogEntry Entry { get; } = entry;

    public string Name => Entry.File;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    /// <summary>0–100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool CanRetry { get; set; }

    [RelayCommand]
    private void Retry() => retry(this);
}

/// <summary>
/// Step 7: the plan's files on the app's one download queue, so they go on after the wizard closes. Each file shows its
/// bytes or "verified"; the total counts only the wizard's files, with rate and time left; Pause and Resume act on the
/// queue; a failed file says why and offers Try again. A tick while a file downloads lets a stall show. Next opens once
/// every file is in place.
/// </summary>
public sealed partial class SetupDownloadingStep(SetupServices services) : SetupStep
{
    private readonly DownloadRate _rate = new(services.Time);
    private IDisposable? _tick;
    private bool _listening;

    public override string Name => Strings.SetupStepDownloading;

    public ObservableCollection<FileProgress> Files { get; } = [];

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string TotalText { get; private set; } = "";

    /// <summary>0–100.</summary>
    [ObservableProperty]
    public partial double TotalProgress { get; private set; }

    [ObservableProperty]
    public partial string Note { get; private set; } = "";

    [ObservableProperty]
    public partial string PauseText { get; private set; } = "";

    [ObservableProperty]
    public partial bool ShowPause { get; private set; }

    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    public override void OnShown()
    {
        if (!_listening)
        {
            _listening = true;
            services.Downloads.Changed += OnChanged;
        }

        Refresh();
    }

    public override void OnHidden()
    {
        if (_listening)
        {
            _listening = false;
            services.Downloads.Changed -= OnChanged;
        }

        _tick?.Dispose();
        _tick = null;
    }

    /// <summary>Shows <paramref name="entries"/> and queues those not in place (a paused queue resumes: the click asked for them).</summary>
    public void Start(IReadOnlyList<CatalogEntry> entries)
    {
        Show(entries);
        foreach (var file in Files.Where(f => !ModelFiles.IsInstalled(services.Paths, f.Entry)))
        {
            Queue(file, () => services.Downloads.Download(file.Entry));
        }

        Refresh();
    }

    /// <summary>
    /// Shows <paramref name="entries"/> as the queue has them, without queuing: a walk resumed after a restart, whose
    /// queue was restored on its own and may be paused on purpose.
    /// </summary>
    public void Show(IReadOnlyList<CatalogEntry> entries)
    {
        if (!Files.Select(f => f.Entry).SequenceEqual(entries))
        {
            Files.Clear();
            foreach (var entry in entries)
            {
                Files.Add(new FileProgress(entry, Retry));
            }
        }

        Refresh();
    }

    [RelayCommand]
    private void PauseOrResume()
    {
        Queue(null, services.Downloads.IsPaused ? (Action)services.Downloads.Resume : services.Downloads.Pause);
        Refresh();
    }

    private void Retry(FileProgress file)
    {
        Queue(file, () => services.Downloads.Download(file.Entry));
        Refresh();
    }

    private void OnChanged() => Refresh(sample: true);

    /// <param name="sample">The queue reported progress, or the tick ran: a sample for the rate.</param>
    private void Refresh(bool sample = false)
    {
        var items = services.Downloads.Items;
        long done = 0;
        long total = 0;
        var failed = false;
        var allInPlace = Files.Count > 0;
        var downloading = false;
        foreach (var file in Files)
        {
            var entry = file.Entry;
            total += entry.Size;
            if (ModelFiles.IsInstalled(services.Paths, entry))
            {
                done += entry.Size;
                file.Text = Format(Strings.SetupDownloadVerified, Units.Size(entry.Size));
                file.Progress = 100;
                file.Error = null;
                file.CanRetry = false;
                continue;
            }

            allInPlace = false;
            var item = items.LastOrDefault(i => i.Entry.Id == entry.Id);
            var bytes = Math.Min(item?.Bytes ?? 0, entry.Size);
            done += bytes;
            file.Progress = 100.0 * bytes / Math.Max(1, entry.Size);
            file.Text = Format(Strings.SettingsModelsProgress, Units.Size(bytes), Units.Size(entry.Size));
            file.Error = item is null ? Strings.SetupDownloadStopped : item.State == DownloadState.Failed ? item.Error : null;
            file.CanRetry = item is null or { State: DownloadState.Failed };
            failed |= file.CanRetry;
            if (item?.State == DownloadState.Downloading)
            {
                downloading = true;
                if (sample)
                {
                    _rate.Add(entry.Id, item.Bytes);
                }
            }
        }

        IsDone = allInPlace;
        var paused = services.Downloads.IsPaused && !IsDone;
        Title = IsDone ? Strings.SetupDownloadedTitle
            : failed ? Strings.SetupDownloadFailedTitle
            : paused ? Strings.SetupDownloadPausedTitle
            : Strings.SetupDownloadingTitle;
        TotalProgress = 100.0 * done / Math.Max(1, total);
        TotalText = IsDone ? Format(Strings.SetupDownloadAllVerified, Units.Size(total))
            : paused ? Format(Strings.SetupDownloadTotalPaused, Units.Size(done), Units.Size(total))
            : downloading && _rate.BytesPerSecond is { } rate && rate > 0
                ? Format(Strings.SetupDownloadTotalRate, Units.Size(done), Units.Size(total), Units.Rate(rate), Units.TimeLeft(TimeSpan.FromSeconds((total - done) / rate)))
                : Format(Strings.SettingsModelsProgress, Units.Size(done), Units.Size(total));
        Note = IsDone ? Strings.SetupDownloadNoteDone : Strings.SetupDownloadNoteBusy;
        ShowPause = !IsDone;
        PauseText = services.Downloads.IsPaused ? Strings.SettingsModelsResume : Strings.SettingsModelsPause;
        CanContinue = IsDone;
        Tick(downloading);
    }

    private void Tick(bool downloading)
    {
        if (downloading && _listening && _tick is null)
        {
            _tick = services.Timers.Every(TimeSpan.FromSeconds(1), () => Refresh(sample: true));
        }
        else if (!(downloading && _listening) && _tick is not null)
        {
            _tick.Dispose();
            _tick = null;
        }
    }

    /// <summary>Queue commands write downloads.json; a full or read-only disk shows on the file, never as a crash.</summary>
    private void Queue(FileProgress? file, Action command)
    {
        try
        {
            command();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            services.Log.Warn($"Download list not updated: {ex.Message}");
            if (file is not null)
            {
                file.Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsQueueFailed, ex.Message);
            }
        }
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
