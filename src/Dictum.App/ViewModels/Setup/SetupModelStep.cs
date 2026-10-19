using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Diagnostics;
using Dictum.Core.History;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A card on step 2: a catalog model, or (<see cref="Id"/> null) the hand-placed file the config uses now.</summary>
/// <param name="Speed">"~6 s for 5 s of speech (estimate)", or null before anything is known.</param>
/// <param name="SpeedBadge">Fast, Usable or "Too slow here", or null with no estimate.</param>
public sealed record ModelChoice(
    string? Id,
    string Name,
    string Note,
    string? Speed,
    string Size,
    bool Recommended,
    bool Installed,
    string? SpeedBadge = null,
    bool TooSlow = false)
{
    /// <summary>What a screen reader says for the card.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Step 2: the GPU and memory line and a card per speech model that understands step 1's languages. The
/// recommendation comes from the shell's probe, as on Settings → Models; speeds from past dictations, a measurement,
/// or the hardware class. A model picked by hand stays picked when the probe answers.
/// </summary>
public sealed partial class SetupModelStep : SetupStep
{
    private const string KeptKey = "";

    private readonly SetupServices _services;
    private readonly string? _keptFile;
    private readonly string? _currentId;
    private IReadOnlyDictionary<string, double> _dictated = new Dictionary<string, double>();
    private HardwareInfo? _hardware;
    private Task? _probe;
    private string? _chosen;
    private string _language = "auto";
    private bool _building;

    /// <param name="keptFile">A model file outside the catalog that the config uses now (<see cref="SetupDefaults.KeptModelFile"/>).</param>
    /// <param name="currentId">The catalog model the config uses now, picked first on a re-run.</param>
    public SetupModelStep(SetupServices services, string? keptFile, string? currentId)
    {
        _services = services;
        _keptFile = keptFile;
        _currentId = currentId;
        HardwareText = Strings.SettingsModelsProbing;
        Build();
    }

    public override string Name => Strings.SetupStepModel;

    [ObservableProperty]
    public partial IReadOnlyList<ModelChoice> Choices { get; private set; } = [];

    [ObservableProperty]
    public partial ModelChoice? Selected { get; set; }

    [ObservableProperty]
    public partial string HardwareText { get; private set; } = "";

    /// <summary>The catalog id to plan with; null keeps the hand-placed file.</summary>
    public string? SpeechModel => Selected?.Id;

    /// <summary>Step 1's setting; a list ("uk, en") is understood only by multilingual models.</summary>
    public string Language
    {
        get => _language;
        set
        {
            _language = value;
            Build();
        }
    }

    /// <summary>Picks the model a resumed walk had: over the hand-placed file and the config's. Null picks the hand-placed file.</summary>
    public void Pick(string? id)
    {
        _chosen = id ?? KeptKey;
        Build();
    }

    public override void OnShown()
    {
        _dictated = ReadDictations();
        Build();
        _ = ProbeAsync();
    }

    /// <summary>Asks the shell's probe once; the line, the recommendation and the speeds follow. A failed probe counts as no GPU.</summary>
    public Task ProbeAsync() => _probe ??= ProbeOnceAsync();

    partial void OnSelectedChanged(ModelChoice? value)
    {
        if (!_building && value is not null)
        {
            _chosen = Key(value);
        }

        CanContinue = value is not null;
        OnPropertyChanged(nameof(SpeechModel));
    }

    private async Task ProbeOnceAsync()
    {
        try
        {
            _hardware = await _services.Hardware();
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"GPU probe failed: {ex.Message}", ex);
            _hardware = new HardwareInfo(null, _services.FreeDiskBytes());
        }

        HardwareText = ModelText.Hardware(_hardware, _services.MemoryBytes);
        Build();
    }

    private void Build()
    {
        var recommended = _hardware is { } hardware ? SetupPlanner.RecommendSpeechModel(hardware) : null;
        var estimates = SpeedEstimator.Estimate(_services.Catalog.Entries, _dictated, _services.Measurements.Ratios, _hardware, _services.CpuCores);
        var choices = new List<ModelChoice>();
        if (_keptFile is not null)
        {
            choices.Add(new ModelChoice(null, Path.GetFileName(_keptFile), Strings.SetupModelKeep, null, Units.Size(FileSize(_keptFile)), false, true));
        }

        foreach (var entry in _services.Catalog.Entries.Where(e => e is { Kind: CatalogKind.Whisper, Hidden: false } && e.Understands(_language)))
        {
            var estimate = estimates.GetValueOrDefault(entry.Id);
            choices.Add(new ModelChoice(
                entry.Id,
                ModelText.Name(entry),
                ModelText.Note(entry),
                estimate is null ? null : ModelText.Speed(estimate),
                Units.Size(entry.Size),
                entry.Id == recommended,
                ModelFiles.IsInstalled(_services.Paths, entry),
                estimate?.Class switch
                {
                    SpeedClass.Fast => Strings.SettingsModelsFast,
                    SpeedClass.Usable => Strings.SettingsModelsUsable,
                    SpeedClass.TooSlow => Strings.SettingsModelsTooSlow,
                    _ => null,
                },
                estimate?.Class == SpeedClass.TooSlow));
        }

        var wanted = _chosen ?? (_keptFile is not null ? KeptKey : _currentId ?? recommended);
        _building = true;
        try
        {
            Choices = choices;
            Selected = choices.FirstOrDefault(c => Key(c) == wanted) ?? choices.FirstOrDefault();
        }
        finally
        {
            _building = false;
        }
    }

    private static string Key(ModelChoice choice) => choice.Id ?? KeptKey;

    private long FileSize(string file)
    {
        try
        {
            return new FileInfo(_services.Paths.ResolveModel(file)).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private IReadOnlyDictionary<string, double> ReadDictations()
    {
        try
        {
            return SpeedEstimator.FromHistory(new HistoryStore(_services.Paths.HistoryFile, int.MaxValue).ReadLast(int.MaxValue));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Log.Warn($"Could not read the history for speed estimates: {ex.Message}");
            return new Dictionary<string, double>();
        }
    }
}
