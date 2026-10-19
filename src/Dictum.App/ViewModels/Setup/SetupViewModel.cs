using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// The setup wizard: eight steps, the step line and strip, and the walk. Step 6 builds the plan from the answers
/// and the config on disk; leaving it queues the missing files (step 7), or saves when nothing is missing. The save runs
/// only with every file in place: the typed API key to the secret store, then <c>config.json</c>, then the login item,
/// as <c>dictum init</c> does. The shell keeps one walk per process, so a closed wizard reopens at the same step.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private const int LanguagesIndex = 0;
    private const int ModelIndex = 1;
    private const int CleanupIndex = 2;
    private const int DownloadsIndex = 5;
    private const int DownloadingIndex = 6;
    private const int ReadyIndex = 7;

    /// <summary>A first setup offers the UI's language and English.</summary>
    private const string English = "en";

    private readonly SetupServices _services;
    private readonly SetupStep[] _steps;
    private readonly string _loaded;
    private bool _downloaded;
    private bool _waiting;

    /// <summary>Step 7 started (or a kept walk resumed) and neither the save nor Back from step 7 has followed.</summary>
    private bool _awaitingFiles;

    /// <param name="resume">A walk kept in setup.json, resumed at step 7 (or at step 3 when its plain key must be typed again).</param>
    public SetupViewModel(SetupServices services, SavedSetup? resume = null)
    {
        _services = services;
        var current = LoadConfig(services, out _);
        if (resume is not null && !MayResume(resume, current, services.Catalog))
        {
            // Not downloading, and kept over another config: the answers are stale.
            SavedSetupFile.Delete(services.Paths, services.Log);
            resume = null;
        }

        _loaded = Snapshot(current);
        var defaults = SetupPlanner.Defaults(current, new HardwareInfo(null, 0), services.Catalog, m => File.Exists(services.Paths.ResolveModel(m)));
        var currentModel = current is not null && current.Stt.GetValueOrDefault(SetupPlanner.SpeechProviderId(current)) is WhisperLocalSttConfig whisper
            ? services.Catalog.ForFile(whisper.Model)?.Id
            : null;

        Languages = new SetupLanguagesStep(resume?.Language ?? (current is null
            ? LanguageList.Format([.. new[] { WhisperLanguages.Fallback(), English }.Distinct()])
            : defaults.Language));
        Model = new SetupModelStep(services, defaults.KeptModelFile, currentModel);
        Cleanup = new SetupCleanupStep(services.Server, defaults.Server);
        Words = new SetupWordsStep(resume?.Words ?? current?.Profiles.FirstOrDefault()?.Vocabulary ?? []);
        Login = new SetupLoginStep(
            services.Autostart is not null && services.Executable is not null,
            resume?.StartAtLogin ?? (current is null || services.Autostart?.Installed is not null));
        Downloads = new SetupDownloadsStep(services);
        Downloading = new SetupDownloadingStep(services);
        Ready = new SetupReadyStep(services.Tray, services.Daemon, services.RestartDaemon);
        _steps = [Languages, Model, Cleanup, Words, Login, Downloads, Downloading, Ready];
        foreach (var step in _steps)
        {
            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SetupStep.CanContinue))
                {
                    Update();
                }
            };
        }

        if (resume is null)
        {
            Go(LanguagesIndex);
        }
        else
        {
            Resume(resume);
        }
    }

    public SetupLanguagesStep Languages { get; }

    public SetupModelStep Model { get; }

    public SetupCleanupStep Cleanup { get; }

    public SetupWordsStep Words { get; }

    public SetupLoginStep Login { get; }

    public SetupDownloadsStep Downloads { get; }

    public SetupDownloadingStep Downloading { get; }

    public SetupReadyStep Ready { get; }

    public IReadOnlyList<SetupStep> Steps => _steps;

    [ObservableProperty]
    public partial int Index { get; private set; }

    [ObservableProperty]
    public partial SetupStep? Current { get; private set; }

    /// <summary>"Step 3 of 8 · Text clean-up", a live region in the view.</summary>
    [ObservableProperty]
    public partial string StepLine { get; private set; } = "";

    /// <summary>The strip: a bar per step, on for the steps done and the current one.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<bool> Bars { get; private set; } = [];

    [ObservableProperty]
    public partial string NextText { get; private set; } = "";

    [ObservableProperty]
    public partial string FootNote { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool CanNext { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial bool CanBack { get; private set; }

    [ObservableProperty]
    public partial bool Saving { get; private set; }

    /// <summary>Why the save stopped, or why the walk went back to step 7.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Finish on step 8: the shell closes the window and forgets this walk.</summary>
    public event Action? Finished;

    /// <summary>The window was closed on step 7 and every file is now in place: the shell shows the walk again, to save.</summary>
    public event Action? FilesReady;

    /// <summary>config.json was just written: the shell tells the supervisor, so the daemon starts at once.</summary>
    public event Action? Saved;

    /// <summary>
    /// Whether the shell may show this walk again: not after the save (step 8, even closed with ✕), and not over a config
    /// that changed since the walk read it, unless its files are downloading.
    /// </summary>
    public bool CanResume => Index != ReadyIndex && (Index == DownloadingIndex || Snapshot(LoadConfig(_services, out _)) == _loaded);

    /// <summary>The window opened, or opened again: the step on screen listens again.</summary>
    public void OnOpened()
    {
        StopWaiting();
        Current?.OnShown();
    }

    /// <summary>
    /// The window closed: the step on screen stops listening; the answers and the step are kept. Closed while its files
    /// download (on step 7, or a kept walk resumed at step 3), the walk waits for them and raises <see cref="FilesReady"/>.
    /// </summary>
    public void OnClosed()
    {
        Current?.OnHidden();
        if (_awaitingFiles && !_waiting && !FilesInPlace())
        {
            _waiting = true;
            _services.Downloads.Changed += OnFilesChanged;
        }
    }

    /// <summary>The daemon link changed (UI thread): step 8 follows it.</summary>
    public void OnLink(DaemonLink link) => Ready.OnLink(link);

    /// <summary>A state or finished event (UI thread): step 8's try line follows it.</summary>
    public void OnEvent(IpcEvent message) => Ready.OnEvent(message);

    partial void OnSavingChanged(bool value) => Update();

    private void OnFilesChanged()
    {
        if (FilesInPlace())
        {
            StopWaiting();
            FilesReady?.Invoke();
        }
    }

    private void StopWaiting()
    {
        if (_waiting)
        {
            _waiting = false;
            _services.Downloads.Changed -= OnFilesChanged;
        }
    }

    private bool FilesInPlace() => Downloading.Files.All(f => ModelFiles.IsInstalled(_services.Paths, f.Entry));

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        switch (Index)
        {
            case DownloadsIndex when Downloads.HasFiles:
                StartDownloading(Downloads.Files);
                break;
            case DownloadsIndex:
                _downloaded = false;
                await SaveAsync();
                break;
            case DownloadingIndex:
                await SaveAsync();
                break;
            case ReadyIndex:
                Finished?.Invoke();
                break;
            default:
                Go(Index + 1);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        if (Index == DownloadingIndex)
        {
            // Left step 7: a restart starts over rather than resume downloads that were stepped back from.
            SavedSetupFile.Delete(_services.Paths, _services.Log);
            _awaitingFiles = false;
        }

        Go(Index == ReadyIndex && !_downloaded ? DownloadsIndex : Index - 1);
    }

    /// <summary>Rebuilds a kept walk. Step 7 shows the files as the restored queue has them; nothing is queued again.</summary>
    private void Resume(SavedSetup saved)
    {
        Model.Language = Languages.Setting;
        Model.Pick(saved.SpeechModel);
        Cleanup.Restore(saved.UseServer, saved.Url, saved.Model, saved.KeyReference, saved.KeyTyped);
        var files = saved.Files.Select(_services.Catalog.Find).OfType<CatalogEntry>().ToList();
        _downloaded = files.Count > 0;
        _awaitingFiles = files.Count > 0;
        if (files.Count > 0)
        {
            Downloading.Show(files);
        }

        Go(saved.KeyTyped ? CleanupIndex : files.Count > 0 ? DownloadingIndex : DownloadsIndex);
    }

    /// <summary>Queues the files, goes to step 7, and keeps the walk in setup.json so a restart returns here.</summary>
    private void StartDownloading(IReadOnlyList<CatalogEntry> files)
    {
        _downloaded = true;
        _awaitingFiles = true;
        Downloading.Start(files);
        Go(DownloadingIndex);
        var key = Cleanup.UseServer ? Cleanup.KeyText.Trim() : "";
        var reference = ConfigValueResolver.IsReference(key);
        SavedSetupFile.Save(_services.Paths, new SavedSetup(
            Languages.Setting, Model.SpeechModel, Cleanup.UseServer, Cleanup.Url.Trim(), Cleanup.Model.Trim(),
            reference ? key : null, key.Length > 0 && !reference, Words.Words, Login.StartAtLogin, [.. files.Select(f => f.Id)],
            SavedSetupFile.Fingerprint(LoadConfig(_services, out _))),
            _services.Log);
    }

    private void Go(int index)
    {
        Current?.OnHidden();
        Error = null;
        Index = index;
        if (index == ModelIndex)
        {
            Model.Language = Languages.Setting;
        }

        if (index == DownloadsIndex)
        {
            LoadDownloads();
        }

        Current = _steps[index];
        Current.OnShown();
        Update();
    }

    private void Update()
    {
        if (_steps is null)
        {
            return;
        }

        var step = _steps[Index];
        StepLine = string.Format(CultureInfo.CurrentCulture, Strings.SetupStepLine, Index + 1, step.Name);
        Bars = [.. Enumerable.Range(0, _steps.Length).Select(i => i <= Index)];
        NextText = Index switch
        {
            DownloadsIndex => Downloads.HasFiles ? Strings.SetupDownload : Strings.SetupContinue,
            DownloadingIndex => Downloading.IsDone ? Strings.SetupContinue : Strings.SetupDownloadingBusy,
            ReadyIndex => Strings.SetupFinish,
            _ => Strings.SetupNext,
        };
        FootNote = Index switch
        {
            DownloadsIndex => Strings.SetupFootConfirm,
            ReadyIndex => Strings.SetupFootProfiles,
            _ => Strings.SetupFootEsc,
        };
        CanNext = step.CanContinue && !Saving;
        CanBack = Index > 0 && !Saving;
    }

    private void LoadDownloads()
    {
        var current = LoadConfig(_services, out var invalid);
        var (plan, error) = BuildPlan(current);
        var wanted = new List<CatalogEntry>();
        if (Model.SpeechModel is { } id && _services.Catalog.Find(id) is { } speech)
        {
            wanted.Add(speech);
        }

        if (_services.Catalog.Find(SetupPlanner.VadId) is { } vad)
        {
            wanted.Add(vad);
        }

        Downloads.Load(plan, error, wanted, Cleanup.Answer(SecretReference(current)).Server, invalid);
    }

    private (SetupPlan? Plan, string? Error) BuildPlan(DictumConfig? current)
    {
        try
        {
            return (SetupPlanner.Plan(current, Answers(current), _services.Catalog, e => ModelFiles.IsInstalled(_services.Paths, e)), null);
        }
        catch (ConfigException ex)
        {
            return (null, Describe(ex));
        }
    }

    private SetupAnswers Answers(DictumConfig? current)
    {
        var (choice, server, _) = Cleanup.Answer(SecretReference(current));
        return new SetupAnswers(Languages.Setting, Model.SpeechModel, choice, server, Words.Words, ReplaceVocabulary: true);
    }

    /// <summary>Key, then config, then login item; only with every file in place.</summary>
    private async Task SaveAsync()
    {
        Saving = true;
        Error = null;
        try
        {
            var current = LoadConfig(_services, out _);
            var (_, _, typed) = Cleanup.Answer(SecretReference(current));
            var plan = SetupPlanner.Plan(current, Answers(current), _services.Catalog, e => ModelFiles.IsInstalled(_services.Paths, e));
            if (plan.Downloads.Count > 0)
            {
                // A file went away after step 7 (deleted, or cancelled on Settings → Models): fetch it again first.
                StartDownloading(plan.Downloads);
                Error = Strings.SetupFilesMissing;
                return;
            }

            if (typed is not null)
            {
                var stored = await ApiKeys.StoreAsync(_services.Secrets, SecretName(current), typed, CancellationToken.None);
                if (stored.Error is { } problem)
                {
                    Error = string.Format(CultureInfo.CurrentCulture, Strings.SetupKeyFailed, problem);
                    return;
                }
            }

            _services.Store.Save(plan.Config);
            SavedSetupFile.Delete(_services.Paths, _services.Log);
            _awaitingFiles = false;
            Saved?.Invoke();
            Ready.Load(plan.Config, await ApplyLoginAsync());
            Go(ReadyIndex);
        }
        catch (ConfigException ex)
        {
            Error = Describe(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = string.Format(CultureInfo.CurrentCulture, Strings.SetupSaveFailed, ex.Message);
        }
        finally
        {
            Saving = false;
        }
    }

    /// <summary>Writes or removes <c>dictum-gui</c>'s login item after the save; a failure is shown on step 8, the setup stands.</summary>
    private async Task<string?> ApplyLoginAsync()
    {
        if (!Login.IsAvailable || _services.Autostart is not { } autostart || _services.Executable is not { } executable)
        {
            return null;
        }

        try
        {
            await autostart.ApplyAsync(Login.StartAtLogin, new LoginItem(LoginApp.DesktopApp, executable), CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn($"Login item not changed: {ex.Message}");
            return string.Format(CultureInfo.CurrentCulture, Strings.SetupLoginFailed, ex.Message);
        }
    }

    /// <summary>The config on disk; null when there is none, or when it cannot be read (<paramref name="invalid"/>: finishing replaces it).</summary>
    private static DictumConfig? LoadConfig(SetupServices services, out bool invalid)
    {
        invalid = false;
        if (!services.Store.Exists)
        {
            return null;
        }

        try
        {
            return services.Store.Load();
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            services.Log.Warn($"config.json could not be read for the setup: {ex.Message}");
            invalid = true;
            return null;
        }
    }

    /// <summary>
    /// A kept walk whose files are downloading resumes at step 7 over any config, as within one process; one that would
    /// resume elsewhere (a typed key, files missing from the catalog) only over the config it was kept on.
    /// </summary>
    private static bool MayResume(SavedSetup saved, DictumConfig? current, Catalog catalog) =>
        (!saved.KeyTyped && saved.Files.Any(id => catalog.Find(id) is not null))
        || saved.BasedOn == SavedSetupFile.Fingerprint(current);

    private static string Snapshot(DictumConfig? config) => config is null ? "" : ConfigStore.Serialize(config);

    /// <summary>The secret the clean-up key goes to: the Settings name, so Settings can replace or remove it.</summary>
    private static string SecretName(DictumConfig? current) =>
        ApiKeys.ProcessorSecretName(SetupPlanner.CleanupProcessorId(current ?? DefaultConfig.Create()));

    private static string SecretReference(DictumConfig? current) => ApiKeys.SecretPrefix + SecretName(current);

    private static string Describe(ConfigException ex) =>
        ex.Issues.Count == 0 ? ex.Message : $"{ex.Message} {string.Join(" ", ex.Issues.Select(i => $"{i.Path}: {i.Message}."))}";
}
