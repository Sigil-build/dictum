using Dictum.App.Resources;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.App.Settings;

/// <summary>
/// The config the Settings pages edit.
/// <list type="bullet">
/// <item>Each edit makes a new record from a deep copy and is checked with <see cref="ConfigValidator"/>.</item>
/// <item>The copy is saved <see cref="SaveDelay"/> after the last edit; a copy with errors is shown, never saved, and a copy equal to
/// the file on disk has nothing to save.</item>
/// <item>The session knows what is on disk: what it loaded, what it saved, and what the config watcher last reported.
/// The watcher reports every save, our own included. A report equal to the known file is ignored; anything else
/// replaces the copy and drops edits not yet saved.</item>
/// <item>A good save changes nothing the pages show, so it raises <see cref="Saved"/>, not <see cref="Changed"/>.</item>
/// </list>
/// Use it on the UI thread.
/// </summary>
public sealed class SettingsSession(ConfigStore store, ITimers timers, ILog log)
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private IDisposable? _pending;
    private DictumConfig? _toSave;
    private string? _known;

    public DictumConfig Config { get; private set; } = new();

    /// <summary>The config last known to be in the file: loaded, saved, or reported by the watcher.</summary>
    public DictumConfig OnDisk { get; private set; } = new();

    public IReadOnlyList<ConfigIssue> Issues { get; private set; } = [];

    /// <summary>Why the pages cannot edit: no config yet, or an invalid file. Null when they can.</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the last save failed; null after a good one.</summary>
    public string? SaveError { get; private set; }

    public bool CanEdit => Problem is null;

    /// <summary>The copy has validator errors, so nothing is saved until they are fixed. An invalid file is <see cref="Problem"/> instead.</summary>
    public bool HasErrors => CanEdit && Issues.Any(i => i.Severity == ConfigIssueSeverity.Error);

    /// <summary>The copy, its issues, the problem or the save error changed.</summary>
    public event Action? Changed;

    public event Action? Saved;

    public void Load()
    {
        Drop();
        try
        {
            var (config, warnings) = store.LoadWithIssues();
            Know(config);
            Config = Copy(config);
            Issues = warnings;
            Problem = null;
        }
        catch (ConfigException ex)
        {
            _known = null;
            Problem = store.Exists ? ex.Message : Strings.SettingsNoConfig;
            Issues = ex.Issues;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _known = null;
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
        if (!Issues.Any(i => i.Severity == ConfigIssueSeverity.Error) && ConfigStore.Serialize(next) != _known)
        {
            _toSave = next;
            _pending = timers.Once(SaveDelay, () => Save());
        }

        Changed?.Invoke();
    }

    /// <summary>The first issue at exactly this config path, such as "general.historySize".</summary>
    public string? IssueAt(string path) => Issues.FirstOrDefault(i => i.Path == path)?.Message;

    /// <summary>Saves a pending edit now (the window is closing, or a provider is about to be tested); true when the file was written.</summary>
    public bool Flush()
    {
        if (_pending is null)
        {
            return false;
        }

        _pending.Dispose();
        _pending = null;
        return Save();
    }

    /// <summary>The config watcher read a valid file.</summary>
    public void OnFileChanged(DictumConfig changed)
    {
        if (CanEdit && ConfigStore.Serialize(changed) == _known)
        {
            return;
        }

        Drop();
        Know(changed);
        Config = Copy(changed);
        Issues = ConfigValidator.Validate(changed);
        Problem = null;
        Changed?.Invoke();
    }

    /// <summary>The config watcher found the file invalid.</summary>
    public void OnFileInvalid(ConfigException error)
    {
        Drop();
        _known = null;
        Problem = error.Message;
        Issues = error.Issues;
        Changed?.Invoke();
    }

    private bool Save()
    {
        _pending = null;
        if (_toSave is not { } config)
        {
            return false;
        }

        _toSave = null;
        var hadError = SaveError is not null;
        try
        {
            store.Save(config);
            Know(config);
            SaveError = null;
            Saved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = UiText.Format(Strings.SettingsSaveFailed, ex.Message);
            log.Warn($"Settings not saved: {ex.Message}");
        }

        if (hadError || SaveError is not null)
        {
            Changed?.Invoke();
        }

        return SaveError is null;
    }

    private void Know(DictumConfig config)
    {
        _known = ConfigStore.Serialize(config);
        OnDisk = Copy(config);
    }

    private void Drop()
    {
        _pending?.Dispose();
        _pending = null;
        _toSave = null;
    }

    private static DictumConfig Copy(DictumConfig config) => ConfigStore.Parse(ConfigStore.Serialize(config));
}
