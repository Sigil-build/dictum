using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;

namespace Dictum.App.ViewModels.Settings;

/// <summary>What the Speech providers page uses besides the config; fakes in tests.</summary>
/// <param name="Models">The model dropdown for a card, given its current model (<see cref="WhisperModels.Choices"/>).</param>
/// <param name="ModelPresent">Whether a model file is in the models folder.</param>
/// <param name="Delay">The wait for the daemon's reload after a save before a test.</param>
public sealed record SpeechServices(
    IDaemonChannel Channel,
    FrameHub Frames,
    ITimers Timers,
    ISecretStore Secrets,
    Func<string?, IReadOnlyList<Option<string>>> Models,
    Func<string, bool> ModelPresent,
    Func<TimeSpan, Task> Delay,
    TimeProvider Time,
    ILog Log);

public enum ProviderKind
{
    Local,
    Remote,
}

/// <summary>
/// One provider on the Speech providers page (spec 7).
/// <list type="bullet">
/// <item>Local Whisper: model, runtime, threads, VAD.</item>
/// <item>Remote server: URL, model, key, timeout.</item>
/// <item>Both: Test, by a 5 s recording with the live meter or by the built-in sample.</item>
/// </list>
/// The daemon tests its own copy of the config, so Test saves pending edits first and gives the daemon time to reload.
/// </summary>
public sealed partial class ProviderCard : ObservableObject
{
    /// <summary>The daemon's watcher waits 300 ms after a write, then applies the config.</summary>
    public static readonly TimeSpan ReloadWait = TimeSpan.FromMilliseconds(1500);

    public const int TestSeconds = 5;

    private readonly SettingsSession _session;
    private readonly SpeechServices _services;
    private readonly Action<ProviderCard> _remove;
    private readonly Func<DateTimeOffset?> _lastSave;
    private readonly InputMeter _meter = new();
    private readonly DateTimeOffset _epoch;
    private bool _loading;
    private bool _timeoutEmpty;
    private Task? _committing;

    /// <param name="lastSave">When the session last wrote the file, whoever asked: the daemon reloads after any write.</param>
    public ProviderCard(string id, ProviderKind kind, SettingsSession session, SpeechServices services, Action<ProviderCard> remove, Func<DateTimeOffset?> lastSave)
    {
        Id = id;
        Kind = kind;
        _session = session;
        _services = services;
        _remove = remove;
        _lastSave = lastSave;
        _epoch = services.Time.GetUtcNow();
    }

    public string Id { get; }

    public ProviderKind Kind { get; }

    public bool IsLocal => Kind == ProviderKind.Local;

    public bool IsRemote => Kind == ProviderKind.Remote;

    public string SecretName => ApiKeys.SecretName(Id);

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string UsedBy { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool CanRemove { get; private set; }

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> Models { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string>? Model { get; set; }

    [ObservableProperty]
    public partial string? ModelError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<WhisperRuntime>> Runtimes { get; private set; } = [];

    [ObservableProperty]
    public partial Option<WhisperRuntime>? Runtime { get; set; }

    [ObservableProperty]
    public partial decimal? Threads { get; set; }

    [ObservableProperty]
    public partial string? ThreadsError { get; private set; }

    [ObservableProperty]
    public partial bool Vad { get; set; }

    [ObservableProperty]
    public partial string? VadNote { get; private set; }

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string? UrlError { get; private set; }

    [ObservableProperty]
    public partial string ModelName { get; set; } = "";

    [ObservableProperty]
    public partial string? ModelNameError { get; private set; }

    /// <summary>The key being typed; cleared once it is committed. The stored key never comes back here.</summary>
    [ObservableProperty]
    public partial string KeyText { get; set; } = "";

    [ObservableProperty]
    public partial string KeyState { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveKeyCommand))]
    public partial bool HasKey { get; private set; }

    [ObservableProperty]
    public partial string? KeyError { get; private set; }

    [ObservableProperty]
    public partial decimal? TimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial string? TimeoutError { get; private set; }

    [ObservableProperty]
    public partial bool Testing { get; private set; }

    [ObservableProperty]
    public partial double MeterLevel { get; private set; }

    [ObservableProperty]
    public partial SystemRow? Result { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(TestSampleCommand))]
    public partial bool CanTest { get; private set; }

    public void Load(DictumConfig config)
    {
        if (!config.Stt.TryGetValue(Id, out var stt))
        {
            return;
        }

        _loading = true;
        try
        {
            var users = config.Profiles.Where(p => p.Stt == Id).Select(p => p.Name ?? p.Id).ToList();
            Title = string.Format(CultureInfo.CurrentCulture, IsLocal ? Strings.SettingsProvidersLocalTitle : Strings.SettingsProvidersRemoteTitle, Id);
            UsedBy = users.Count == 0
                ? Strings.SettingsProvidersUnused
                : string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersUsedBy, string.Join(", ", users));
            CanEdit = _session.CanEdit;
            CanRemove = CanEdit && users.Count == 0;
            UpdateCanTest();
            var path = $"stt.{Id}";
            switch (stt)
            {
                case WhisperLocalSttConfig local:
                    var models = _services.Models(local.Model);
                    if (!models.SequenceEqual(Models))
                    {
                        Models = models;
                    }

                    Model = Models.FirstOrDefault(m => string.Equals(m.Value, local.Model, StringComparison.OrdinalIgnoreCase));
                    ModelError = _session.IssueAt($"{path}.model");
                    Runtimes = RuntimesFor(local.Runtime);
                    Runtime = Runtimes.First(r => r.Value == local.Runtime);
                    Threads = local.Threads;
                    ThreadsError = _session.IssueAt($"{path}.threads");
                    Vad = local.Vad;
                    VadNote = local.Vad && !_services.ModelPresent(local.VadModel)
                        ? string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersVadMissing, local.VadModel)
                        : null;
                    break;
                case OpenAiCompatibleSttConfig remote:
                    Url = remote.BaseUrl;
                    UrlError = _session.IssueAt($"{path}.baseUrl");
                    ModelName = remote.Model;
                    ModelNameError = _session.IssueAt($"{path}.model");
                    HasKey = remote.ApiKey is not null;
                    KeyState = remote.ApiKey switch
                    {
                        null => Strings.SettingsProvidersNoKey,
                        var key when key.StartsWith(ApiKeys.SecretPrefix, StringComparison.Ordinal) => Strings.SettingsProvidersKeyStored,
                        var key when key.StartsWith("env:", StringComparison.Ordinal) =>
                            string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersKeyEnv, key["env:".Length..]),
                        _ => Strings.SettingsProvidersKeyPlain,
                    };
                    if (!_timeoutEmpty)
                    {
                        TimeoutSeconds = remote.TimeoutMs / 1000m;
                        TimeoutError = _session.IssueAt($"{path}.timeoutMs");
                    }

                    break;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnModelChanged(Option<string>? value)
    {
        if (value is not null)
        {
            EditLocal(l => l with { Model = value.Value });
        }
    }

    partial void OnRuntimeChanged(Option<WhisperRuntime>? value)
    {
        if (value is not null)
        {
            EditLocal(l => l with { Runtime = value.Value });
        }
    }

    // Empty means automatic: 0 threads picks a default from the CPU count.
    partial void OnThreadsChanged(decimal? value) => EditLocal(l => l with { Threads = (int)decimal.Truncate(value ?? 0) });

    partial void OnVadChanged(bool value) => EditLocal(l => l with { Vad = value });

    partial void OnUrlChanged(string value) => EditRemote(r => r with { BaseUrl = value.Trim() });

    partial void OnModelNameChanged(string value) => EditRemote(r => r with { Model = value.Trim() });

    partial void OnTimeoutSecondsChanged(decimal? value)
    {
        if (_loading)
        {
            return;
        }

        if (value is not { } seconds)
        {
            // As General's History field (4a review C1): nothing typed on the way to empty is saved.
            _timeoutEmpty = true;
            var saved = (_session.OnDisk.Stt.GetValueOrDefault(Id) as OpenAiCompatibleSttConfig)?.TimeoutMs ?? 60_000;
            EditRemote(r => r with { TimeoutMs = saved });
            TimeoutError = Strings.SettingsNumberRequired;
            return;
        }

        _timeoutEmpty = false;
        EditRemote(r => r with { TimeoutMs = (int)decimal.Truncate(seconds * 1000) });
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => _remove(this);

    /// <summary>Enter or leaving the key field: the typed key goes to the secret store; an empty field keeps the stored key.</summary>
    [RelayCommand]
    private Task CommitKeyAsync()
    {
        if (KeyText.Length == 0)
        {
            return _committing ?? Task.CompletedTask;
        }

        var typed = KeyText;
        KeyText = "";
        _committing = StoreKeyAsync(typed);
        return _committing;
    }

    private async Task StoreKeyAsync(string typed)
    {
        var result = await ApiKeys.StoreAsync(_services.Secrets, SecretName, typed, CancellationToken.None);
        if (result.Error is { } error)
        {
            KeyError = string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersKeyFailed, error);
            return;
        }

        KeyError = null;
        EditRemote(r => r with { ApiKey = result.Value });
    }

    [RelayCommand(CanExecute = nameof(HasKey))]
    private async Task RemoveKeyAsync()
    {
        var current = (_session.Config.Stt.GetValueOrDefault(Id) as OpenAiCompatibleSttConfig)?.ApiKey;
        EditRemote(r => r with { ApiKey = null });
        await ApiKeys.RemoveAsync(_services.Secrets, SecretName, current, _session.Config, _services.Log, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task TestAsync() => RunTestAsync(sample: false);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task TestSampleAsync() => RunTestAsync(sample: true);

    private async Task RunTestAsync(bool sample)
    {
        if (Testing)
        {
            return;
        }

        // A typed key, or one still being stored behind a keyring prompt, is part of what the test must use (final review I1).
        await CommitKeyAsync();
        if (_session.HasErrors)
        {
            Result = new SystemRow(Strings.SettingsProvidersTest, HealthLevel.Error, Strings.SettingsProvidersFixFirst);
            return;
        }

        Testing = true;
        Result = null;
        UpdateCanTest();
        IDisposable? tick = null;
        try
        {
            // The daemon reloads after any write: the pending one flushed now, or a debounced one just before the click (final review I1).
            _session.Flush();
            if (_lastSave() is { } saved && ReloadWait - (_services.Time.GetUtcNow() - saved) is var wait && wait > TimeSpan.Zero)
            {
                await _services.Delay(wait);
            }

            if (!sample)
            {
                tick = _services.Timers.Every(GeneralPage.MeterTick, Tick);
            }

            var request = new IpcRequest("test-stt", Stt: Id, Seconds: sample ? null : TestSeconds, Sample: sample ? true : null);
            var answer = await _services.Channel.TrySendAsync(request, CancellationToken.None, TimeSpan.FromSeconds(TestSeconds + 120));
            Result = answer switch
            {
                null => new SystemRow(Strings.SettingsProvidersTest, HealthLevel.Error, Strings.SettingsProvidersNoDaemon),
                { Ok: true, Test: { } test } => new SystemRow(Strings.SettingsProvidersTest, HealthLevel.Ok,
                    string.Format(CultureInfo.CurrentCulture, Strings.SettingsProvidersRecognised, test.LatencyMs),
                    test.Text.Length > 0 ? test.Text : Strings.SettingsProvidersNothingHeard),
                _ => new SystemRow(Strings.SettingsProvidersTest, HealthLevel.Error, answer.Error ?? Strings.SettingsProvidersTestFailed),
            };
        }
        finally
        {
            tick?.Dispose();
            _meter.Reset();
            MeterLevel = 0;
            Testing = false;
            UpdateCanTest();
        }
    }

    private void Tick()
    {
        _meter.Update(_services.Frames.Rms(InputMeter.Stale), _services.Time.GetUtcNow() - _epoch);
        MeterLevel = _meter.Level;
    }

    private void UpdateCanTest() => CanTest = _session.CanEdit && !_session.HasErrors && !Testing;

    private IReadOnlyList<Option<WhisperRuntime>> RuntimesFor(WhisperRuntime current)
    {
        List<Option<WhisperRuntime>> runtimes =
        [
            new(WhisperRuntime.Auto, Strings.SettingsProvidersRuntimeAuto),
            new(WhisperRuntime.Cpu, Strings.SettingsProvidersRuntimeCpu),
            new(WhisperRuntime.Vulkan, "Vulkan"),
        ];

        // No CUDA runtime ships; a config that already names it keeps it (plan ruling).
        if (current == WhisperRuntime.Cuda)
        {
            runtimes.Add(new(WhisperRuntime.Cuda, "CUDA"));
        }

        return runtimes.SequenceEqual(Runtimes) ? Runtimes : runtimes;
    }

    private void EditLocal(Func<WhisperLocalSttConfig, WhisperLocalSttConfig> change) =>
        Edit(s => s is WhisperLocalSttConfig local ? change(local) : s);

    private void EditRemote(Func<OpenAiCompatibleSttConfig, OpenAiCompatibleSttConfig> change) =>
        Edit(s => s is OpenAiCompatibleSttConfig remote ? change(remote) : s);

    private void Edit(Func<SttConfig, SttConfig> change)
    {
        if (_loading)
        {
            return;
        }

        _session.Edit(c =>
        {
            if (c.Stt.TryGetValue(Id, out var stt))
            {
                c.Stt[Id] = change(stt);
            }

            return c;
        });
    }
}
