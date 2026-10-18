using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Vocabulary;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// Step 4 (spec 8): names, apps and terms, one per line or as a pasted list. A re-run shows the first profile's words,
/// and the save replaces the list with what is left here. The box keeps the text as typed; it is split only for the plan.
/// </summary>
public sealed partial class SetupWordsStep(IReadOnlyList<string> current) : SetupStep
{
    public override string Name => Strings.SetupStepWords;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Join('\n', current);

    /// <summary>The list for the plan: split on lines, commas and semicolons, trimmed, repeats dropped.</summary>
    public IReadOnlyList<string> Words => VocabularyList.Normalize(WordList.Split(Text + "\n").Words);
}
