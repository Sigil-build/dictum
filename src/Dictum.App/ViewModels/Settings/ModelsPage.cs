using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Host.Ipc;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the Models page uses besides the config; fakes in tests.</summary>
/// <param name="Hardware">The GPU probe; the shell runs it once, off the UI thread (spec 10.1).</param>
/// <param name="FreeDiskBytes">Free space where the models are; <see cref="long.MaxValue"/> when unknown.</param>
/// <param name="MemoryBytes">The computer's memory, for the GPU and memory line.</param>
/// <param name="CpuCores">For the CPU speed anchors (spec 10.2).</param>
/// <param name="Delay">Waits for the daemon's reload before a measurement, as the provider test does (4b I1).</param>
public sealed record ModelsServices(
    AppPaths Paths,
    Catalog Catalog,
    IModelDownloads Downloads,
    Func<Task<HardwareInfo>> Hardware,
    Func<long> FreeDiskBytes,
    long MemoryBytes,
    int CpuCores,
    SpeedMeasurements Measurements,
    IDaemonChannel Channel,
    Func<TimeSpan, Task> Delay,
    TimeProvider Time,
    ILog Log);

/// <summary>
/// Settings → Models (spec 7, design SettingsModels): the disk line, the GPU and memory line, and a card per speech
/// model of the catalog. The cards act on the shared download queue (spec 9). The card list is fixed, so a card keeps
/// its controls while it changes (4c C1). Queue changes arrive only while the page is shown.
/// </summary>
public sealed partial class ModelsPage : SettingsPage
{
    /// <summary>Two runs of the 6.9 s sample on a slow CPU, a model load included.</summary>
    public static readonly TimeSpan MeasureTimeout = TimeSpan.FromMinutes(2);

    private readonly SettingsSession _session;
    private readonly ModelsServices _services;
    private readonly DownloadRate _rate;
    private IReadOnlyDictionary<string, double> _dictated = new Dictionary<string, double>();
    private HardwareInfo? _hardware;
    private Task? _probe;
    private bool _listening;
    private DateTimeOffset? _lastSave;

    public ModelsPage(SettingsSession session, ModelsServices services)
    {
        _session = session;
        _services = services;
        _rate = new DownloadRate(services.Time);
        Cards =
        [
            .. services.Catalog.Entries
                .Where(e => e.Kind == CatalogKind.Whisper && (!e.Hidden || ModelFiles.IsInstalled(services.Paths, e)))
                .Select(e => new ModelCard(e, this)),
        ];
        session.Saved += () =>
        {
            _lastSave = services.Time.GetUtcNow();

            // A good save raises no Changed; the file on disk is what "Used by" and Delete also follow.
            Refresh();
        };
        session.Changed += () => Refresh();
        Refresh();
    }

    public override string Title => Strings.SettingsModels;

    public IReadOnlyList<ModelCard> Cards { get; }

    [ObservableProperty]
    public partial string DiskText { get; private set; } = "";

    /// <summary>The models' share of their used and free space, 0–100, for the header's bar.</summary>
    [ObservableProperty]
    public partial double DiskShare { get; private set; }

    [ObservableProperty]
    public partial string HardwareText { get; private set; } = "";

    public override void OnShown()
    {
        if (!_listening)
        {
            _listening = true;
            _services.Downloads.Changed += OnDownloads;
        }

        _dictated = ReadDictations();
        Refresh();
        _ = ProbeAsync();
    }

    public override void OnHidden()
    {
        if (_listening)
        {
            _listening = false;
            _services.Downloads.Changed -= OnDownloads;
        }
    }

    /// <summary>Finds the GPU once; the line, the recommendation and the estimates follow. A failed probe counts as no GPU.</summary>
    public Task ProbeAsync() => _probe ??= ProbeOnceAsync();

    internal void Download(ModelCard card) => Queue(card, () => _services.Downloads.Download(card.Entry));

    internal void Pause(ModelCard card) => Queue(card, _services.Downloads.Pause);

    internal void Resume(ModelCard card) => Queue(card, _services.Downloads.Resume);

    internal void Cancel(ModelCard card) => Queue(card, () => _services.Downloads.Cancel(card.Entry.Id));

    /// <summary>Deletes the file; refused while a profile uses it, even when the command comes without the button (spec 7).</summary>
    internal void Delete(ModelCard card)
    {
        if (!card.CanDelete)
        {
            return;
        }

        card.Error = null;
        card.CheckText = null;
        try
        {
            File.Delete(_services.Paths.ResolveModel(card.Entry.File));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            card.Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsDeleteFailed, ex.Message);
        }

        Refresh();
    }

    /// <summary>Hashes the file off the UI thread and compares it with the catalog (spec 14: SHA-256 on Verify).</summary>
    internal async Task VerifyAsync(ModelCard card)
    {
        card.Error = null;
        card.CheckText = Strings.SettingsModelsVerifying;
        card.Busy = true;
        try
        {
            var path = _services.Paths.ResolveModel(card.Entry.File);
            var matches = await Task.Run(() => FileDownloader.MatchesAsync(card.Entry, path, CancellationToken.None));
            card.CheckText = matches ? Strings.SettingsModelsVerified : Strings.SettingsModelsVerifyMismatch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            card.CheckText = null;
            card.Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsVerifyFailed, ex.Message);
        }
        finally
        {
            card.Busy = false;
        }
    }

    /// <summary>Points the profile's local Whisper provider at this file; profiles that share the provider follow it.</summary>
    internal void UseIn(ModelCard card, string profileId)
    {
        if (LocalProfiles(_session.Config).FirstOrDefault(p => p.Id == profileId) is not { } profile)
        {
            return;
        }

        _session.Edit(c =>
        {
            if (c.Stt.GetValueOrDefault(profile.Provider) is WhisperLocalSttConfig local)
            {
                c.Stt[profile.Provider] = local with { Model = card.Entry.File };
            }

            return c;
        });
    }

    /// <summary>
    /// Times the built-in sample on the provider a profile uses with this file, twice: the first run may include
    /// loading the model, so the faster one counts (spec 10.2, source 2).
    /// </summary>
    internal async Task MeasureAsync(ModelCard card)
    {
        var file = _services.Paths.ResolveModel(card.Entry.File);
        if (LocalProfiles(_session.Config).FirstOrDefault(p => SamePath(_services.Paths.ResolveModel(p.Model), file)) is not { } profile)
        {
            return;
        }

        card.Error = null;
        card.CheckText = Strings.SettingsModelsMeasuring;
        card.Busy = true;
        try
        {
            // The daemon reloads after a save; a test sent before that would run the previous model (4b I1).
            _session.Flush();
            if (_lastSave is { } saved && ProviderCard.ReloadWait - (_services.Time.GetUtcNow() - saved) is var wait && wait > TimeSpan.Zero)
            {
                await _services.Delay(wait);
            }

            double? best = null;
            for (var run = 0; run < 2; run++)
            {
                var answer = await _services.Channel.TrySendAsync(new IpcRequest("test-stt", Stt: profile.Provider, Sample: true), CancellationToken.None, MeasureTimeout);
                if (answer is not { Ok: true, Test: { AudioSeconds: > 0 } test })
                {
                    card.CheckText = null;
                    card.Error = answer is null
                        ? Strings.SettingsProvidersNoDaemon
                        : string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsMeasureFailed, answer.Error ?? Strings.SettingsProvidersTestFailed);
                    return;
                }

                var ratio = test.LatencyMs / 1000.0 / test.AudioSeconds.GetValueOrDefault();
                best = Math.Min(best ?? ratio, ratio);
            }

            _services.Measurements.Set(card.Entry.File, best.GetValueOrDefault());
            card.CheckText = null;
        }
        finally
        {
            card.Busy = false;
            Refresh();
        }
    }

    internal static List<LocalProfile> LocalProfiles(DictumConfig config)
    {
        var profiles = new List<LocalProfile>();
        foreach (var profile in config.Profiles)
        {
            if (config.Stt.GetValueOrDefault(profile.Stt) is WhisperLocalSttConfig local)
            {
                profiles.Add(new LocalProfile(profile.Id, profile.Name ?? profile.Id, profile.Stt, local.Model));
            }
        }

        return profiles;
    }

    /// <summary>Queue commands write downloads.json; a full or read-only disk shows on the card, never as a crash (4a I3).</summary>
    private static void Queue(ModelCard card, Action command)
    {
        card.Error = null;
        try
        {
            command();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            card.Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsQueueFailed, ex.Message);
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private async Task ProbeOnceAsync()
    {
        try
        {
            _hardware = await _services.Hardware();
        }
        catch (Exception ex)
        {
            // A broken Vulkan driver must not take Settings down (review focus 5).
            _services.Log.Warn($"GPU probe failed: {ex.Message}", ex);
            _hardware = new HardwareInfo(null, _services.FreeDiskBytes());
        }

        Refresh();
    }

    private void OnDownloads() => Refresh(sample: true);

    /// <param name="sample">The queue reported progress: a sample for the download rate.</param>
    private void Refresh(bool sample = false)
    {
        var paths = _services.Paths;
        var items = _services.Downloads.Items;
        var profiles = LocalProfiles(_session.Config);
        var saved = LocalProfiles(_session.OnDisk);
        var estimates = SpeedEstimator.Estimate(_services.Catalog.Entries, _dictated, _services.Measurements.Ratios, _hardware, _services.CpuCores);
        var recommended = _hardware is { } hardware ? SetupPlanner.RecommendSpeechModel(hardware) : null;
        foreach (var card in Cards)
        {
            var file = paths.ResolveModel(card.Entry.File);
            var download = items.LastOrDefault(i => i.Entry.Id == card.Entry.Id);
            card.Update(new ModelCardState(
                ModelFiles.IsInstalled(paths, card.Entry),
                download,
                ProgressFor(download, sample),
                estimates.GetValueOrDefault(card.Entry.Id),
                card.Entry.Id == recommended,
                recommended == SetupPlanner.GpuModel,
                [.. profiles.Where(p => SamePath(paths.ResolveModel(p.Model), file))],
                [.. saved.Where(p => SamePath(paths.ResolveModel(p.Model), file))],
                profiles,
                _session.CanEdit));
        }

        ShowDisk();
        HardwareText = _hardware is { } probed ? ModelText.Hardware(probed, _services.MemoryBytes) : Strings.SettingsModelsProbing;
    }

    /// <summary>"949 MB of 1.53 GB", with the rate and time left once the download has a second of progress (review focus 2).</summary>
    private string? ProgressFor(QueuedDownload? item, bool sample)
    {
        if (item is not { State: DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused })
        {
            return null;
        }

        var done = Units.Size(Math.Min(item.Bytes, item.Entry.Size));
        var total = Units.Size(item.Entry.Size);
        if (item.State == DownloadState.Downloading)
        {
            if (sample)
            {
                _rate.Add(item.Entry.Id, item.Bytes);
            }

            if (_rate.Id == item.Entry.Id && _rate.BytesPerSecond is { } rate && _rate.TimeLeft(item.Entry.Size) is { } left)
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsProgressRate, done, total, Units.Rate(rate), Units.TimeLeft(left));
            }
        }

        return string.Format(CultureInfo.CurrentCulture, Strings.SettingsModelsProgress, done, total);
    }

    private void ShowDisk()
    {
        long used = 0;
        try
        {
            var folder = new DirectoryInfo(_services.Paths.ModelsDirectory);
            if (folder.Exists)
            {
                used = folder.EnumerateFiles().Sum(f => f.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The line is a hint; a folder that cannot be listed counts as empty.
        }

        var free = _services.FreeDiskBytes();
        DiskText = ModelText.Disk(used, free);
        DiskShare = free == long.MaxValue || used + free <= 0 ? 0 : 100.0 * used / (used + free);
    }

    private IReadOnlyDictionary<string, double> ReadDictations()
    {
        try
        {
            return SpeedEstimator.FromHistory(new HistoryStore(_services.Paths.HistoryFile, int.MaxValue).ReadLast(int.MaxValue));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn($"Could not read the history for speed estimates: {ex.Message}");
            return new Dictionary<string, double>();
        }
    }
}
