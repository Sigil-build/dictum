using Dictum.Core.Configuration;

namespace Dictum.App.Settings;

/// <summary>
/// The downloads folder the desktop app uses right now: config.json's <c>general.downloadsDirectory</c>, or the folder a
/// first setup chose and has not saved yet, since the wizard downloads before it writes the config.
/// </summary>
/// <remarks>
/// Changed on the UI thread only. The download queue reads <see cref="Current"/> from its own thread through the shell's
/// paths; the folder never changes while the queue has work, so such a read never races a change that matters.
/// </remarks>
public sealed class DownloadsLocation
{
    private volatile string? _configured;
    private volatile string? _pending;

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The folder config.json names; null for the data folder.</summary>
    public string? Configured => _configured;

    /// <summary>The wizard's choice before its save; null when it has none.</summary>
    public string? Pending => _pending;

    /// <summary>Where downloads go: <see cref="Pending"/>, else <see cref="Configured"/>; null for the data folder.</summary>
    public string? Current => _pending ?? _configured;

    /// <summary>The config was read or changed on disk; null when there is none.</summary>
    public void OnConfig(DictumConfig? config) => Update(config?.General.DownloadsDirectory, _pending);

    /// <summary>Settings → Models switched to <paramref name="folder"/> and is saving the config.</summary>
    public void Use(string? folder) => Update(folder, _pending);

    /// <summary>The wizard chose <paramref name="folder"/>, or let go of its choice with null.</summary>
    public void Choose(string? folder) => Update(_configured, folder);

    /// <summary>At start: the config, and the folder of a walk kept in setup.json, which its downloads continue into.</summary>
    public void Restore(DictumConfig? config, string? keptWizardChoice) => Update(config?.General.DownloadsDirectory, keptWizardChoice);

    private void Update(string? configured, string? pending)
    {
        var before = Current;
        _configured = Blank(configured);
        _pending = Blank(pending);
        if (!string.Equals(before, Current, StringComparison.Ordinal))
        {
            Changed?.Invoke();
        }
    }

    private static string? Blank(string? folder) => string.IsNullOrWhiteSpace(folder) ? null : folder;
}
