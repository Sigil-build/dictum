using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
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
/// Settings → History (spec 7): the history file, newest first and at most General → History size, filtered by the
/// search. Copy takes the final text; Delete removes the entry from the file. The file is read each time the page
/// shows; dictated text never travels over IPC (spec 4.1).
/// </summary>
public sealed partial class HistoryPage : SettingsPage
{
    private readonly SettingsSession _session;
    private readonly string _file;
    private readonly ITextClipboard _clipboard;
    private readonly ILog _log;
    private IReadOnlyList<HistoryItem> _all = [];

    public HistoryPage(SettingsSession session, string historyFile, ITextClipboard clipboard, ILog log)
    {
        _session = session;
        _file = historyFile;
        _clipboard = clipboard;
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

    public override void OnShown() => Reload();

    partial void OnSearchChanged(string value) => Filter();

    [RelayCommand]
    private async Task CopyAsync(HistoryItem? item)
    {
        if (item is not null)
        {
            await _clipboard.SetTextAsync(item.Entry.Text);
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
        Selected = Items.FirstOrDefault(i => i.Entry.At == selected) ?? Items.FirstOrDefault();
        Empty = Items.Count > 0 ? null
            : _session.Config.General.HistorySize <= 0 ? Strings.SettingsHistoryOff
            : search.Length > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsHistoryNoMatch, search)
            : Strings.SettingsHistoryNone;
    }
}
