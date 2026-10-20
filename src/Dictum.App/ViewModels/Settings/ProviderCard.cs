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
    Func<string?, IReadOnlyList<Choice<string>>> Models,
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
/// One provider on the Speech providers page.
/// <list type="bullet">
/// <item>Local Whisper: model, runtime, threads, VAD.</item>
/// <item>Remote server: URL, model, <see cref="Key"/>, timeout.</item>
/// <item>Both: Test, by a recording of <see cref="TestSeconds"/> seconds with the live meter or by the built-in sample.</item>
/// </list>
/// The daemon tests its own copy of the config, so Test saves pending edits first and gives the daemon time to reload.
/// </summary>
public sealed partial class ProviderCard : ObservableObject
{
    /// <summary>The daemon's watcher waits 300 ms after a write, then applies the config.</summary>
    public static readonly TimeSpan ReloadWait = TimeSpan.FromMilliseconds(1500);

    private const int TestSeconds = IpcProtocol.DefaultTestSeconds;

    /// <summary>The recording, then two minutes for the daemon to load the model and transcribe.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(TestSeconds) + TimeSpan.FromMinutes(2);

    private readonly SettingsSession _session;
    private readonly SpeechServices _services;
    private readonly Action<ProviderCard> _remove;
    private readonly Func<DateTimeOffset?> _lastSave;
    private readonly InputMeter _meter = new();
    private readonly DateTimeOffset _epoch;
    private bool _loading;
    private bool _timeoutEmpty;

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
        var target = new KeyTarget(
            SecretName,
            () => (_session.Config.Stt.GetValueOrDefault(Id) as OpenAiCompatibleSttConfig)?.ApiKey,
            value => EditRemote(r => r with { ApiKey = value }));
        Key = new ApiKeyField(() => target, () => _session.Config, services.Secrets, services.Log);
    }

    public string Id { get; }

    public ProviderKind Kind { get; }

    public bool IsLocal => Kind == ProviderKind.Local;

    public bool IsRemote => Kind == ProviderKind.Remote;

    public string SecretName => SecretNames.Stt(Id);

    public ApiKeyField Key { get; }

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
    public partial IReadOnlyList<Choice<string>> Models { get; private set; } = [];

    [ObservableProperty]
    public partial Choice<string>? Model { get; set; }

    [ObservableProperty]
    public partial string? ModelError { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<Choice<WhisperRuntime>> Runtimes { get; private set; } = [];

    [ObservableProperty]
    public partial Choice<WhisperRuntime>? Runtime { get; set; }

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
            Title = UiText.Format(IsLocal ? Strings.SettingsProvidersLocalTitle : Strings.SettingsProvidersRemoteTitle, Id);
            UsedBy = users.Count == 0
                ? Strings.SettingsProvidersUnused
                : UiText.Format(Strings.SettingsProvidersUsedBy, string.Join(", ", users));
            CanEdit = _session.CanEdit;
            CanRemove = CanEdit && users.Count == 0;
            UpdateCanTest();
            var path = ConfigPaths.Stt(Id);
            switch (stt)
            {
                case WhisperLocalSttConfig local:
                    LoadLocal(local, path);
                    break;
                case OpenAiCompatibleSttConfig remote:
                    LoadRemote(remote, path);
                    break;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Waits until <see cref="ReloadWait"/> has passed since <paramref name="lastSave"/>, so the daemon tests the saved config.</summary>
    internal static Task WaitForReloadAsync(DateTimeOffset? lastSave, TimeProvider time, Func<TimeSpan, Task> delay) =>
        lastSave is { } saved && ReloadWait - (time.GetUtcNow() - saved) is var wait && wait > TimeSpan.Zero
            ? delay(wait)
            : Task.CompletedTask;

    partial void OnModelChanged(Choice<string>? value)
    {
        if (value is not null)
        {
            EditLocal(l => l with { Model = value.Value });
        }
    }

    partial void OnRuntimeChanged(Choice<WhisperRuntime>? value)
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
            // As General's History field: nothing typed on the way to empty is saved.
            _timeoutEmpty = true;
            var saved = (_session.OnDisk.Stt.GetValueOrDefault(Id) as OpenAiCompatibleSttConfig)?.TimeoutMs ?? OpenAiCompatibleSttConfig.DefaultTimeoutMs;
            EditRemote(r => r with { TimeoutMs = saved });
            TimeoutError = Strings.SettingsNumberRequired;
            return;
        }

        _timeoutEmpty = false;
        EditRemote(r => r with { TimeoutMs = TimeoutFields.Milliseconds(seconds) });
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => _remove(this);

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

        // A typed key, or one still being stored behind a keyring prompt, is part of what the test must use.
        await Key.CommitAsync();
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
            // The daemon reloads after any write: the pending one flushed now, or a debounced one just before the click.
            _session.Flush();
            await WaitForReloadAsync(_lastSave(), _services.Time, _services.Delay);

            if (!sample)
            {
                tick = _services.Timers.Every(GeneralPage.MeterTick, Tick);
            }

            var request = new IpcRequest(IpcCommand.TestStt, Stt: Id, Seconds: sample ? null : TestSeconds, Sample: sample ? true : null);
            var answer = await _services.Channel.TrySendAsync(request, CancellationToken.None, TestTimeout);
            Result = answer switch
            {
                null => new SystemRow(Strings.SettingsProvidersTest, HealthLevel.Error, Strings.SettingsProvidersNoDaemon),
                { Ok: true, Test: { } test } => new SystemRow(Strings.SettingsProvidersTest, HealthLevel.Ok,
                    UiText.Format(Strings.SettingsProvidersRecognised, test.LatencyMs),
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

    private void LoadLocal(WhisperLocalSttConfig local, string path)
    {
        var models = _services.Models(local.Model);
        if (!models.SequenceEqual(Models))
        {
            Models = models;
        }

        Model = Models.FirstOrDefault(m => string.Equals(m.Value, local.Model, StringComparison.OrdinalIgnoreCase));
        ModelError = _session.IssueAt(ConfigPaths.Field(path, nameof(WhisperLocalSttConfig.Model)));
        Runtimes = RuntimesFor(local.Runtime);
        Runtime = Runtimes.First(r => r.Value == local.Runtime);
        Threads = local.Threads;
        ThreadsError = _session.IssueAt(ConfigPaths.Field(path, nameof(WhisperLocalSttConfig.Threads)));
        Vad = local.Vad;
        VadNote = local.Vad && !_services.ModelPresent(local.VadModel)
            ? UiText.Format(Strings.SettingsProvidersVadMissing, local.VadModel)
            : null;
    }

    private void LoadRemote(OpenAiCompatibleSttConfig remote, string path)
    {
        Url = remote.BaseUrl;
        UrlError = _session.IssueAt(ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.BaseUrl)));
        ModelName = remote.Model;
        ModelNameError = _session.IssueAt(ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.Model)));
        Key.Show(remote.ApiKey);
        if (!_timeoutEmpty)
        {
            TimeoutSeconds = TimeoutFields.Seconds(remote.TimeoutMs);
            TimeoutError = _session.IssueAt(ConfigPaths.Field(path, nameof(OpenAiCompatibleSttConfig.TimeoutMs)));
        }
    }

    private IReadOnlyList<Choice<WhisperRuntime>> RuntimesFor(WhisperRuntime current)
    {
        List<Choice<WhisperRuntime>> runtimes =
        [
            new(WhisperRuntime.Auto, Strings.SettingsProvidersRuntimeAuto),
            new(WhisperRuntime.Cpu, Strings.SettingsProvidersRuntimeCpu),
            new(WhisperRuntime.Vulkan, "Vulkan"),
        ];

        // No CUDA runtime ships; a config that already names it keeps it.
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
