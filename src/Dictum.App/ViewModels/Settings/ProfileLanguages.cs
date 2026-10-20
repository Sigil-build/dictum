using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// The languages of the profile being edited: one, several (as chips, with a warning for closely related ones) or
/// automatic. The mode shown is kept while the list says otherwise, so "Several" stays when one chip is left.
/// </summary>
public sealed partial class ProfileLanguages : ObservableObject
{
    private readonly SettingsSession _session;
    private readonly Func<ProfileConfig?> _current;
    private readonly Action<Func<ProfileConfig, ProfileConfig>> _edit;
    private LanguageMode? _shownMode;
    private bool _loading;

    /// <param name="current">The profile being edited.</param>
    /// <param name="edit">Changes the profile being edited.</param>
    public ProfileLanguages(SettingsSession session, Func<ProfileConfig?> current, Action<Func<ProfileConfig, ProfileConfig>> edit)
    {
        _session = session;
        _current = current;
        _edit = edit;
        Modes =
        [
            new(LanguageMode.One, Strings.SettingsProfilesLanguageOne),
            new(LanguageMode.Several, Strings.SettingsProfilesLanguageSeveral),
            new(LanguageMode.Auto, Strings.SettingsProfilesLanguageAuto),
        ];
        All = [.. WhisperLanguages.Codes.Select(code => new Choice<string>(code, WhisperLanguages.Name(code)))
            .OrderBy(o => o.Label, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))];
    }

    public IReadOnlyList<Choice<LanguageMode>> Modes { get; }

    public IReadOnlyList<Choice<string>> All { get; }

    [ObservableProperty]
    public partial Choice<LanguageMode>? Mode { get; set; }

    [ObservableProperty]
    public partial bool ShowsOne { get; private set; }

    [ObservableProperty]
    public partial bool ShowsSeveral { get; private set; }

    [ObservableProperty]
    public partial bool ShowsAuto { get; private set; }

    /// <summary>The language when the mode is One.</summary>
    [ObservableProperty]
    public partial Choice<string>? One { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<LanguageChip> Chips { get; private set; } = [];

    [ObservableProperty]
    public partial Choice<string>? ToAdd { get; set; }

    [ObservableProperty]
    public partial string? SimilarWarning { get; private set; }

    /// <summary>A validator warning on the languages other than the similar pair, such as a list sent to a remote server.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    /// <summary>Another profile is shown: the mode follows its setting again.</summary>
    public void Reset() => _shownMode = null;

    public void Load(ProfileConfig profile, string path)
    {
        _loading = true;
        try
        {
            var setting = LanguageSetting.Parse(profile.Language, _shownMode);
            Mode = Modes.Single(m => m.Value == setting.Mode);
            ShowsOne = setting.Mode == LanguageMode.One;
            ShowsSeveral = setting.Mode == LanguageMode.Several;
            ShowsAuto = setting.Mode == LanguageMode.Auto;
            var pairs = LanguageList.SimilarPairs(setting.Codes);
            var warned = pairs.SelectMany(p => new[] { p.First, p.Second }).ToHashSet();
            var removable = setting.Codes.Count > 1;
            Chips = [.. setting.Codes.Select(code => new LanguageChip(code, WhisperLanguages.Name(code), warned.Contains(code),
                new RelayCommand(() => Edit(s => s.Without(code)), () => removable)))];
            One = ShowsOne ? All.FirstOrDefault(o => o.Value == setting.Codes[0]) : null;
            ToAdd = null;
            SimilarWarning = pairs.Count > 0
                ? UiText.Format(Strings.SettingsProfilesSimilar, WhisperLanguages.Name(pairs[0].First), WhisperLanguages.Name(pairs[0].Second))
                : null;
            var similarText = LanguageList.SimilarWarning(setting.Codes);
            Note = _session.Issues.FirstOrDefault(i => i.Path == ConfigPaths.Field(path, nameof(ProfileConfig.Language)) && i.Message != similarText)?.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnModeChanged(Choice<LanguageMode>? value)
    {
        if (value is not null)
        {
            Edit(s => s.WithMode(value.Value, WhisperLanguages.Fallback()));
        }
    }

    partial void OnOneChanged(Choice<string>? value)
    {
        if (value is not null)
        {
            Edit(s => s.With(value.Value));
        }
    }

    partial void OnToAddChanged(Choice<string>? value)
    {
        if (value is not null)
        {
            Edit(s => s.With(value.Value));
        }
    }

    private void Edit(Func<LanguageSetting, LanguageSetting> change)
    {
        if (_loading)
        {
            return;
        }

        var next = change(LanguageSetting.Parse(_current()?.Language, _shownMode));
        _shownMode = next.Mode;
        _edit(p => p with { Language = next.Format() });
    }
}
