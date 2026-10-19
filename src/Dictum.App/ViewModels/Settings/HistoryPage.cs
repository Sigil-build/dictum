using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;

namespace Dictum.App.ViewModels.Settings;

/// <summary>One dictation in the list: the entry and its texts for the row.</summary>
public sealed record HistoryItem(HistoryEntry Entry, string When, string Profile, string? Language, string FirstLine, string Duration)
{
    public string? Problem => Entry.Error is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryFailed, Entry.Error);

    public static HistoryItem Of(HistoryEntry entry) => new(
        entry,
        entry.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        entry.ProfileId,
        entry.Language?.ToUpperInvariant(),
        entry.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "",
        string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistorySeconds, entry.AudioSeconds));
}

/// <summary>
/// Settings → History: the history file, newest first and at most General → History size, filtered by the search.
/// Copy takes the final text; Delete removes the entry from the file. The file is read each time the page shows;
/// dictated text never travels over IPC.
/// </summary>
public sealed partial class HistoryPage : SettingsPage, IDisposable
{
    private readonly SettingsSession _session;
    private readonly string _file;
    private readonly ITextClipboard _clipboard;
    private readonly IProcessorRunner _runner;
    private readonly ILog _log;
    private IReadOnlyList<HistoryItem> _all = [];
    private CancellationTokenSource? _reRun;
    private int _reRunCount;

    public HistoryPage(SettingsSession session, string historyFile, ITextClipboard clipboard, IProcessorRunner runner, ILog log)
    {
        _session = session;
        _file = historyFile;
        _clipboard = clipboard;
        _runner = runner;
        _log = log;
    }

    public override string Title => Strings.SettingsHistory;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<HistoryItem> Items { get; private set; } = [];

    [ObservableProperty]
    public partial HistoryItem? Selected { get; set; }

    /// <summary>Why the list is empty: history off, nothing yet, or no match.</summary>
    [ObservableProperty]
    public partial string? Empty { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> Chains { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string>? Chain { get; set; }

    [ObservableProperty]
    public partial bool HasChains { get; private set; }

    [ObservableProperty]
    public partial string? ReRunText { get; private set; }

    [ObservableProperty]
    public partial string? ReRunLabel { get; private set; }

    [ObservableProperty]
    public partial string? ReRunError { get; private set; }

    [ObservableProperty]
    public partial bool ReRunning { get; private set; }

    public override void OnShown()
    {
        LoadChains();
        Reload();
    }

    public override void OnHidden() => _reRun?.Cancel();

    partial void OnChainChanged(Option<string>? value)
    {
        if (value is not null)
        {
            _ = ReRunAsync(value.Value);
        }
    }

    partial void OnSelectedChanged(HistoryItem? value)
    {
        _reRun?.Cancel();
        _reRunCount++;
        ReRunning = false;
        ReRunText = null;
        ReRunLabel = null;
        ReRunError = null;
    }

    [RelayCommand]
    private async Task CopyReRunAsync()
    {
        if (ReRunText is not { } text)
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            // A busy clipboard throws; a command that throws would take dictum-gui down.
            Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryCopyFailed, ex.Message);
            _log.Warn($"Re-run result not copied: {ex.Message}");
        }
    }

    private void LoadChains()
    {
        var config = _session.Config;
        Chains = [.. config.Profiles.Where(p => p.Pipeline.Count > 0).Select(p => new Option<string>(p.Id,
            string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryChain, p.Name ?? p.Id, string.Join(" → ", p.Pipeline))))];
        HasChains = Chains.Count > 0;
    }

    /// <summary>
    /// The entry's raw text through a profile's chain, in the GUI. Only the newest run shows, and nothing is
    /// written to the history file or the clipboard.
    /// </summary>
    private async Task ReRunAsync(string profileId)
    {
        var config = _session.Config;
        if (Selected is not { } item || config.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
        {
            return;
        }

        _reRun?.Cancel();
        var cancel = _reRun = new CancellationTokenSource();
        var run = ++_reRunCount;
        ReRunning = true;
        try
        {
            List<(string Id, ProcessorConfig Config)> steps = [.. profile.Pipeline.Where(config.Processors.ContainsKey).Select(id => (id, config.Processors[id]))];
            var result = await _runner.RunAsync(steps, item.Entry.RawText, ProcessorRunner.ContextFor(profile), cancel.Token);
            if (run != _reRunCount)
            {
                return;
            }

            ReRunText = result.Text;
            ReRunLabel = string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryReRunLabel, profile.Name ?? profile.Id, result.Milliseconds);
            ReRunError = result.Error;
        }
        catch (OperationCanceledException)
        {
            // Another entry was chosen, or the page left.
        }
        finally
        {
            if (run == _reRunCount)
            {
                ReRunning = false;
            }
        }
    }

    partial void OnSearchChanged(string value) => Filter();

    [RelayCommand]
    private async Task CopyAsync(HistoryItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(item.Entry.Text);
            Error = null;
        }
        catch (Exception ex)
        {
            // A busy clipboard throws; a command that throws would take dictum-gui down.
            Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryCopyFailed, ex.Message);
            _log.Warn($"History text not copied: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Delete(HistoryItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Store().Delete(item.Entry.At);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"History entry not deleted: {ex.Message}");
        }

        Reload();
    }

    public void Dispose()
    {
        _reRun?.Cancel();
        _reRun?.Dispose();
        _reRun = null;
    }

    private HistoryStore Store() => new(_file, Math.Max(_session.Config.General.HistorySize, 0));

    private void Reload()
    {
        var size = _session.Config.General.HistorySize;
        try
        {
            _all = size <= 0 ? [] : [.. Store().ReadLast(size).Reverse().Select(HistoryItem.Of)];
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _all = [];
            Error = string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryReadFailed, ex.Message);
            _log.Warn($"History not read: {ex.Message}");
        }

        Filter();
    }

    private void Filter()
    {
        var search = Search.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Has(string text) => compare.IndexOf(text, search, CompareOptions.IgnoreCase) >= 0;

        var selected = Selected?.Entry.At;
        Items = search.Length == 0 ? _all : [.. _all.Where(i => Has(i.Entry.Text) || Has(i.Entry.RawText) || Has(i.Entry.ProfileId))];
        Selected = Items.FirstOrDefault(i => i.Entry.At == selected) ?? (Items.Count > 0 ? Items[0] : null);
        Empty = Items.Count > 0 ? null
            : _session.Config.General.HistorySize <= 0 ? Strings.SettingsHistoryOff
            : search.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryNoMatch, search)
            : Strings.SettingsHistoryNone;
    }
}
