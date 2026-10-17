using System.Globalization;
using Dictum.App.Resources;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

/// <summary>
/// The config the Settings pages edit (spec 7, "Saving").
/// <list type="bullet">
/// <item>Each edit makes a new record from a deep copy and is checked with <see cref="ConfigValidator"/>.</item>
/// <item>The copy is saved 500 ms after the last edit; a copy with errors is shown, never saved.</item>
/// <item>The config watcher reports every save, our own included: our own text is recognised and ignored, while
/// anything else replaces the copy and drops edits not yet saved.</item>
/// </list>
/// Use it on the UI thread.
/// </summary>
public sealed class SettingsSession(ConfigStore store, ITimers timers, ILog log)
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private IDisposable? _pending;
    private DictumConfig? _toSave;
    private string? _lastWritten;

    public DictumConfig Config { get; private set; } = new();

    public IReadOnlyList<ConfigIssue> Issues { get; private set; } = [];

    /// <summary>Why the pages cannot edit: no config yet, or an invalid file. Null when they can.</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the last save failed; null after a good one.</summary>
    public string? SaveError { get; private set; }

    public bool CanEdit => Problem is null;

    /// <summary>The copy, its issues, the problem or the save error changed.</summary>
    public event Action? Changed;

    /// <summary>The file was written.</summary>
    public event Action? Saved;

    public void Load()
    {
        Drop();
        try
        {
            var (config, warnings) = store.LoadWithIssues();
            Config = config;
            Issues = warnings;
            Problem = null;
        }
        catch (ConfigException ex)
        {
            Problem = store.Exists ? ex.Message : Strings.SettingsNoConfig;
            Issues = ex.Issues;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = ex.Message;
            Issues = [];
        }

        Changed?.Invoke();
    }

    /// <summary>Applies a page's change to a deep copy of the config.</summary>
    public void Edit(Func<DictumConfig, DictumConfig> change)
    {
        if (!CanEdit)
        {
            return;
        }

        var next = change(Copy(Config));
        Config = next;
        Issues = ConfigValidator.Validate(next);
        Drop();
        if (!Issues.Any(i => i.Severity == ConfigIssueSeverity.Error))
        {
            _toSave = next;
            _pending = timers.Once(SaveDelay, Save);
        }

        Changed?.Invoke();
    }

    /// <summary>The first issue at exactly this config path, such as "general.historySize".</summary>
    public string? IssueAt(string path) => Issues.FirstOrDefault(i => i.Path == path)?.Message;

    /// <summary>Saves a pending edit now: the window is closing.</summary>
    public void Flush()
    {
        if (_pending is null)
        {
            return;
        }

        _pending.Dispose();
        _pending = null;
        Save();
    }

    /// <summary>The config watcher read a valid file.</summary>
    public void OnFileChanged(DictumConfig changed)
    {
        if (ConfigStore.Serialize(changed) == _lastWritten)
        {
            return;
        }

        Drop();
        Config = changed;
        Issues = ConfigValidator.Validate(changed);
        Problem = null;
        Changed?.Invoke();
    }

    /// <summary>The config watcher found the file invalid.</summary>
    public void OnFileInvalid(ConfigException error)
    {
        Drop();
        Problem = error.Message;
        Issues = error.Issues;
        Changed?.Invoke();
    }

    private void Save()
    {
        _pending = null;
        if (_toSave is not { } config)
        {
            return;
        }

        _toSave = null;
        try
        {
            store.Save(config);
            _lastWritten = ConfigStore.Serialize(config);
            SaveError = null;
            Saved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = string.Format(CultureInfo.CurrentCulture, Strings.SettingsSaveFailed, ex.Message);
            log.Warn($"Settings not saved: {ex.Message}");
        }

        Changed?.Invoke();
    }

    private void Drop()
    {
        _pending?.Dispose();
        _pending = null;
        _toSave = null;
    }

    private static DictumConfig Copy(DictumConfig config) => ConfigStore.Parse(ConfigStore.Serialize(config));
}
