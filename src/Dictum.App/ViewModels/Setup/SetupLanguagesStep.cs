using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Setup;

/// <summary>A language on step 1: its own name and code, picked or not, and whether the search shows it.</summary>
public sealed partial class LanguageRow(string code) : ObservableObject
{
    public string Code { get; } = code;

    public string Name { get; } = WhisperLanguages.Name(code);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;
}

/// <summary>
/// Step 1 (spec 8, design): a row per Whisper language, the picked ones first in the order picked; a search by name or
/// code that keeps picked rows in sight; "detect any language"; and the similar-language banner (spec 11). Next needs a
/// language, or "detect any".
/// </summary>
public sealed partial class SetupLanguagesStep : SetupStep
{
    private readonly List<string> _picked = [];

    /// <param name="setting">The language setting to start from: "uk, en", "uk" or "auto".</param>
    public SetupLanguagesStep(string setting)
    {
        var codes = LanguageList.Parse(setting);
        var known = (codes ?? []).Where(WhisperLanguages.Codes.Contains).ToList();
        var byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        Rows =
        [
            .. known.Select(code => new LanguageRow(code) { IsChecked = true }),
            .. WhisperLanguages.Codes.Except(known).Select(code => new LanguageRow(code)).OrderBy(r => r.Name, byName),
        ];
        _picked.AddRange(known);
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

    /// <summary>The config's language setting: the codes in the order picked ("uk, en"), or "auto".</summary>
    public string Setting => LanguageList.Format(DetectAny ? [] : _picked);

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

    private void Filter()
    {
        var search = Search.Trim();
        foreach (var row in Rows)
        {
            row.IsShown = search.Length == 0
                || row.IsChecked
                || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || row.Code.StartsWith(search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Update()
    {
        CanContinue = DetectAny || _picked.Count > 0;
        BannerLead = !DetectAny && LanguageList.SimilarPairs(_picked) is [var pair, ..]
            ? string.Format(CultureInfo.CurrentCulture, Strings.SetupSimilarLead, WhisperLanguages.Name(pair.First), WhisperLanguages.Name(pair.Second))
            : null;
        OnPropertyChanged(nameof(Setting));
    }
}
