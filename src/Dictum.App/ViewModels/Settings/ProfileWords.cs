using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>The vocabulary of the profile being edited, as chips; words come in from a box and leave by their chip.</summary>
public sealed partial class ProfileWords : ObservableObject
{
    private readonly SettingsSession _session;
    private readonly Action<Func<ProfileConfig, ProfileConfig>> _edit;

    /// <param name="edit">Changes the profile being edited.</param>
    public ProfileWords(SettingsSession session, Action<Func<ProfileConfig, ProfileConfig>> edit)
    {
        _session = session;
        _edit = edit;
    }

    [ObservableProperty]
    public partial IReadOnlyList<WordChip> Chips { get; private set; } = [];

    [ObservableProperty]
    public partial string Count { get; private set; } = "";

    [ObservableProperty]
    public partial string? Note { get; private set; }

    /// <summary>
    /// Text from the words box: complete words are added, and the part still being typed comes back for the box. The
    /// view owns the box, since a reset made inside the box's own binding write never reaches it.
    /// </summary>
    public string Add(string text)
    {
        var (words, typing) = WordList.Split(text);
        if (words.Count == 0)
        {
            return text;
        }

        _edit(p => p with { Vocabulary = WordList.Add(p.Vocabulary, words) });
        return typing;
    }

    public void Load(ProfileConfig profile, string path)
    {
        Chips = [.. profile.Vocabulary.Select(word => new WordChip(word,
            new RelayCommand(() => _edit(p => p with { Vocabulary = [.. p.Vocabulary.Where(w => w != word)] }))))];
        Count = UiText.Format(Strings.SettingsProfilesWordCount, profile.Vocabulary.Count);
        Note = _session.Issues.FirstOrDefault(i => i.Path.StartsWith(ConfigPaths.Field(path, nameof(ProfileConfig.Vocabulary)), StringComparison.Ordinal))?.Message;
    }
}
