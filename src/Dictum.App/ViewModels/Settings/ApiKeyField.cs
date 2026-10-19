using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Settings;

/// <summary>Where a key field's key goes: the secret it is stored under, the config value it has now, and how a new value is written.</summary>
public sealed record KeyTarget(string SecretName, Func<string?> Current, Action<string?> Apply);

/// <summary>
/// The API key field of an LLM processor or a remote speech provider. Enter, or leaving the field, sends the typed key
/// to the secret store; an empty field keeps the stored key, which never comes back here. The store may answer after
/// another provider is shown (a keyring prompt on Linux), so the answer goes to the target the key was typed for.
/// </summary>
public sealed partial class ApiKeyField : ObservableObject
{
    private readonly Func<KeyTarget?> _target;
    private readonly Func<DictumConfig> _config;
    private readonly ISecretStore _secrets;
    private readonly ILog _log;
    private Task? _committing;

    /// <param name="target">The provider the field shows now; null when it shows none.</param>
    /// <param name="config">The config after an edit, to tell whether another provider still uses a stored key.</param>
    public ApiKeyField(Func<KeyTarget?> target, Func<DictumConfig> config, ISecretStore secrets, ILog log)
    {
        _target = target;
        _config = config;
        _secrets = secrets;
        _log = log;
    }

    /// <summary>The key being typed; cleared once it is committed.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string State { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool HasKey { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows what the config holds for the key: stored, from a variable, plain, or none.</summary>
    public void Show(string? apiKey)
    {
        HasKey = apiKey is not null;
        State = ApiKeys.Describe(apiKey);
    }

    /// <summary>
    /// Sends the typed key to the store. With nothing new typed it returns the store still in flight, so a caller can
    /// wait for a key that is behind a keyring prompt.
    /// </summary>
    public Task CommitAsync()
    {
        if (Text.Length == 0 || _target() is not { } target)
        {
            return _committing ?? Task.CompletedTask;
        }

        var typed = Text;
        Text = "";
        _committing = StoreAsync(target, typed);
        return _committing;
    }

    /// <summary>Enter, or leaving the field.</summary>
    [RelayCommand]
    private Task Commit() => CommitAsync();

    [RelayCommand(CanExecute = nameof(HasKey))]
    private async Task RemoveAsync()
    {
        if (_target() is not { } target)
        {
            return;
        }

        var current = target.Current();
        target.Apply(null);
        await ApiKeys.RemoveAsync(_secrets, target.SecretName, current, _config(), _log, CancellationToken.None);
    }

    private async Task StoreAsync(KeyTarget target, string typed)
    {
        var result = await ApiKeys.StoreAsync(_secrets, target.SecretName, typed, CancellationToken.None);
        var shown = _target()?.SecretName == target.SecretName;
        if (result.Error is { } error)
        {
            if (shown)
            {
                Error = UiText.Format(Strings.SettingsProvidersKeyFailed, error);
            }

            return;
        }

        if (shown)
        {
            Error = null;
        }

        var previous = target.Current();
        target.Apply(result.Value);
        await StoredKeys.DeleteLegacyCleanupAsync(_secrets, previous, _config(), _log, CancellationToken.None);
    }
}
