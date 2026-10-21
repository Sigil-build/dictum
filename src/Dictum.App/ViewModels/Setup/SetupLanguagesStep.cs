using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.Shell;
using Dictum.Core.Configuration;
using Dictum.Core.Docs;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// A language on step 1: its own name and code, picked or not, whether the search shows it, and whether it is the main
/// language or a picked one that "Make main" can put first.
/// </summary>
public sealed partial class LanguageRow(string code, Action<LanguageRow> makeMain) : ObservableObject
{
    public string Code { get; } = code;

    public string Name { get; } = WhisperLanguages.Name(code);

    /// <summary>What the search matches: the language's own name, the English one and the UI language's.</summary>
    public IReadOnlyList<string> Names { get; } = WhisperLanguages.Names(code);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>The first language of the setting, which Whisper keeps unless it is sure of another.</summary>
    [ObservableProperty]
    public partial bool IsMain { get; internal set; }

    /// <summary>Picked, but not the main language: <see cref="MakeMainCommand"/> makes it so.</summary>
    [ObservableProperty]
    public partial bool OffersMain { get; internal set; }

    [RelayCommand]
    private void MakeMain() => makeMain(this);
}

/// <summary>
/// Step 1: a row per Whisper language, the picked ones first in the order picked; a search by name or code that keeps
/// picked rows in sight; "detect any language"; and the similar-language banner. Next needs a language, or "detect any".
/// The setting lists the main language first: the one the user made main, else the UI language when picked, else the
/// first picked. A link under the lead opens the documentation's "How it works".
/// </summary>
public sealed partial class SetupLanguagesStep : SetupStep
{
    private readonly List<string> _picked = [];
    private readonly string _uiLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
    private readonly OpenDocs? _openDocs;

    /// <summary>The language made main, which is the main one whenever it is picked: a config's or a kept walk's first, or the user's pick.</summary>
    private string? _chosenMain;

    /// <param name="setting">The language setting to start from: "uk, en", "uk" or "auto".</param>
    /// <param name="fresh">The setting is a first setup's default, not the user's own list: the main language follows the UI's.</param>
    /// <param name="openDocs">Opens the documentation; the wizard passes the shell's. Left out, the link does nothing (tests).</param>
    public SetupLanguagesStep(string setting, bool fresh = false, OpenDocs? openDocs = null)
    {
        _openDocs = openDocs;
        var codes = LanguageList.Parse(setting);
        var known = (codes ?? []).Where(WhisperLanguages.Codes.Contains).ToList();
        var byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        Rows =
        [
            .. known.Select(code => new LanguageRow(code, MakeMain) { IsChecked = true }),
            .. WhisperLanguages.Codes.Except(known).Select(code => new LanguageRow(code, MakeMain)).OrderBy(r => r.Name, byName),
        ];
        _picked.AddRange(known);
        _chosenMain = fresh ? null : known.FirstOrDefault();
        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }

        DetectAny = codes is [];
        Update();
    }

    public override string Name => Strings.SetupStepLanguages;

    public IReadOnlyList<LanguageRow> Rows { get; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool DetectAny { get; set; }

    /// <summary>"Українська and Беларуская are closely related." for the first similar pair picked; null when none.</summary>
    [ObservableProperty]
    public partial string? BannerLead { get; private set; }

    /// <summary>The config's language setting: the main language, then the others in the order picked ("uk, en"), or "auto".</summary>
    public string Setting => LanguageList.Format(DetectAny ? [] : Main is { } main ? [main, .. _picked.Where(c => c != main)] : []);

    private string? Main =>
        _chosenMain is { } chosen && _picked.Contains(chosen) ? chosen
        : _picked.Contains(_uiLanguage) ? _uiLanguage
        : _picked.FirstOrDefault();

    /// <summary>"How does Dictum work?": the overview's section on what happens to a dictation.</summary>
    [RelayCommand]
    private void HowItWorks() => _openDocs?.Invoke(DocsPage.Home, DocsAnchors.HowItWorks);

    partial void OnSearchChanged(string value) => Filter();

    partial void OnDetectAnyChanged(bool value) => Update();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LanguageRow.IsChecked) || sender is not LanguageRow row)
        {
            return;
        }

        if (row.IsChecked && !_picked.Contains(row.Code))
        {
            _picked.Add(row.Code);
        }
        else if (!row.IsChecked)
        {
            _picked.Remove(row.Code);
        }

        Update();
        Filter();
    }

    private void MakeMain(LanguageRow row)
    {
        if (row.IsChecked)
        {
            _chosenMain = row.Code;
            Update();
        }
    }

    private void Filter()
    {
        var search = Search.Trim();
        foreach (var row in Rows)
        {
            row.IsShown = search.Length == 0
                || row.IsChecked
                || row.Names.Any(name => name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                || row.Code.StartsWith(search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Update()
    {
        var main = DetectAny ? null : Main;
        foreach (var row in Rows)
        {
            row.IsMain = row.Code == main;
            row.OffersMain = main is not null && row.IsChecked && row.Code != main;
        }

        CanContinue = DetectAny || _picked.Count > 0;
        BannerLead = !DetectAny && LanguageList.SimilarPairs(_picked) is [var pair, ..]
            ? UiText.Format(Strings.SetupSimilarLead, WhisperLanguages.Name(pair.First), WhisperLanguages.Name(pair.Second))
            : null;
        OnPropertyChanged(nameof(Setting));
    }
}
