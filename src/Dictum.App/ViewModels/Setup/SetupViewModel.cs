using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// The setup wizard (spec 8): eight steps, the step line and strip, and the walk. Step 6 builds the plan from the answers
/// and the config on disk; leaving it queues the missing files (step 7), or saves when nothing is missing. The save runs
/// only with every file in place: the typed API key to the secret store, then <c>config.json</c>, then the login item,
/// as <c>dictum init</c> does. The shell keeps one walk per process, so a closed wizard reopens at the same step.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private const int DownloadsIndex = 5;
    private const int DownloadingIndex = 6;
    private const int ReadyIndex = 7;

    private readonly SetupServices _services;
    private readonly SetupStep[] _steps;
    private bool _downloaded;

    public SetupViewModel(SetupServices services)
    {
        _services = services;
        var current = LoadConfig(services, out _);
        var defaults = SetupPlanner.Defaults(current, new HardwareInfo(null, 0), services.Catalog, m => File.Exists(services.Paths.ResolveModel(m)));
        var currentModel = current is not null && current.Stt.GetValueOrDefault(SetupPlanner.SpeechProviderId(current)) is WhisperLocalSttConfig whisper
            ? services.Catalog.ForFile(whisper.Model)?.Id
            : null;

        Languages = new SetupLanguagesStep(current is null
            ? LanguageList.Format([.. new[] { WhisperLanguages.Fallback(), "en" }.Distinct()])
            : defaults.Language);
        Model = new SetupModelStep(services, defaults.KeptModelFile, currentModel);
        Cleanup = new SetupCleanupStep(services.Server, defaults.Server);
        Words = new SetupWordsStep(current?.Profiles.FirstOrDefault()?.Vocabulary ?? []);
        Login = new SetupLoginStep(services.Autostart is not null && services.Executable is not null, current is null || services.Autostart?.Installed is not null);
        Downloads = new SetupDownloadsStep(services);
        Downloading = new SetupDownloadingStep(services);
        Ready = new SetupReadyStep(services.Tray);
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

        Go(0);
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

    /// <summary>The window opened, or opened again: the step on screen listens again.</summary>
    public void OnOpened() => Current?.OnShown();

    /// <summary>The window closed: the step on screen stops listening; the answers and the step are kept.</summary>
    public void OnClosed() => Current?.OnHidden();

    partial void OnSavingChanged(bool value) => Update();

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        switch (Index)
        {
            case DownloadsIndex when Downloads.HasFiles:
                _downloaded = true;
                Downloading.Start(Downloads.Files);
                Go(DownloadingIndex);
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
    private void Back() => Go(Index == ReadyIndex && !_downloaded ? DownloadsIndex : Index - 1);

    private void Go(int index)
    {
        Current?.OnHidden();
        Error = null;
        Index = index;
        if (index == 1)
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

    /// <summary>Key, then config, then login item; only with every file in place (review focus 1).</summary>
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
                _downloaded = true;
                Downloading.Start(plan.Downloads);
                Go(DownloadingIndex);
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

    /// <summary>The secret the clean-up key goes to: the Settings name, so Settings can replace or remove it (code map G5).</summary>
    private static string SecretName(DictumConfig? current) =>
        ApiKeys.ProcessorSecretName(SetupPlanner.CleanupProcessorId(current ?? DefaultConfig.Create()));

    private static string SecretReference(DictumConfig? current) => ApiKeys.SecretPrefix + SecretName(current);

    private static string Describe(ConfigException ex) =>
        ex.Issues.Count == 0 ? ex.Message : $"{ex.Message} {string.Join(" ", ex.Issues.Select(i => $"{i.Path}: {i.Message}."))}";
}
