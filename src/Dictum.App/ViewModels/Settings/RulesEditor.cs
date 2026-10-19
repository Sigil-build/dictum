using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// One row of the replace-rules table: Find (plain text, or a regex), Replace, Match case, Whole word, On. The last row
/// is a blank one: it is not in the config, and it becomes a rule once its Find has text.
/// </summary>
public sealed partial class RuleRow(RulesEditor editor) : ObservableObject
{
    private bool _loading;

    public int Index { get; private set; }

    [ObservableProperty]
    public partial string Find { get; set; } = "";

    [ObservableProperty]
    public partial string Replace { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRegex { get; set; }

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool On { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool IsDraft { get; private set; }

    [ObservableProperty]
    public partial bool CanWholeWord { get; private set; } = true;

    internal void Load(int index, ReplaceRule rule, string? error)
    {
        _loading = true;
        Index = index;
        IsDraft = false;

        // A rule with nothing to find keeps the Regex box as ticked: a reset here, inside the box's own binding write,
        // never reaches the box, and the pattern typed next would be saved as plain text.
        IsRegex = !string.IsNullOrEmpty(rule.Pattern) || (string.IsNullOrEmpty(rule.From) && IsRegex);
        Find = (IsRegex ? rule.Pattern : rule.From) ?? "";
        Replace = rule.To;
        MatchCase = !rule.IgnoreCase;
        WholeWord = rule.WholeWord;
        On = !rule.Disabled;
        CanWholeWord = !IsRegex;
        Error = Find.Length == 0 ? Strings.SettingsRulesFindEmpty : error;
        _loading = false;
    }

    internal void LoadDraft(int index)
    {
        _loading = true;
        Index = index;
        IsDraft = true;
        Find = "";
        Replace = "";
        IsRegex = false;
        MatchCase = false;
        WholeWord = false;
        On = true;
        CanWholeWord = true;
        Error = null;
        _loading = false;
    }

    internal ReplaceRule ToRule() => new()
    {
        From = IsRegex ? null : Find,
        Pattern = IsRegex ? Find : null,
        To = Replace,
        IgnoreCase = !MatchCase,
        WholeWord = !IsRegex && WholeWord,
        Disabled = !On,
    };

    partial void OnFindChanged(string value) => Changed();

    partial void OnReplaceChanged(string value) => Changed();

    partial void OnIsRegexChanged(bool value)
    {
        CanWholeWord = !value;
        Changed();
    }

    partial void OnMatchCaseChanged(bool value) => Changed();

    partial void OnWholeWordChanged(bool value) => Changed();

    partial void OnOnChanged(bool value) => Changed();

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => editor.RemoveAt(Index);

    private bool CanRemove() => !IsDraft;

    private void Changed()
    {
        if (_loading)
        {
            return;
        }

        if (IsDraft)
        {
            // The blank row waits for something to find; its other cells ride along when it becomes a rule.
            if (Find.Length > 0)
            {
                editor.Add(this);
            }

            return;
        }

        editor.Set(Index, ToRule());
    }
}

/// <summary>
/// The replace-rules table of the Processors page. Edits go through the session by position within the processor. Rows
/// are kept and reloaded in place, and the collection itself changes slot by slot, so the cell being typed in keeps its
/// control and its focus when the blank row becomes a rule.
/// </summary>
public sealed partial class RulesEditor(SettingsSession session) : ObservableObject
{
    private string? _id;
    private List<RuleRow> _rows = [];

    public ObservableCollection<RuleRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string? ImportNote { get; private set; }

    public void Load(string id, DictumConfig config)
    {
        if (config.Processors.GetValueOrDefault(id) is not ReplaceProcessorConfig replace)
        {
            return;
        }

        if (_id != id)
        {
            _id = id;
            _rows = [];
            Rows.Clear();
            ImportNote = null;
        }

        var rows = new List<RuleRow>();
        for (var i = 0; i < replace.Rules.Count; i++)
        {
            var row = i < _rows.Count ? _rows[i] : new RuleRow(this);
            var path = ConfigPaths.Item(ConfigPaths.Processor(id, nameof(ReplaceProcessorConfig.Rules)), i);
            row.Load(i, replace.Rules[i], session.Issues.FirstOrDefault(issue => issue.Path.StartsWith(path, StringComparison.Ordinal))?.Message);
            rows.Add(row);
        }

        var draft = replace.Rules.Count < _rows.Count ? _rows[replace.Rules.Count] : new RuleRow(this);
        draft.LoadDraft(replace.Rules.Count);
        rows.Add(draft);
        _rows = rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == Rows.Count)
            {
                Rows.Add(rows[i]);
            }
            else if (!ReferenceEquals(Rows[i], rows[i]))
            {
                Rows[i] = rows[i];
            }
        }

        while (Rows.Count > rows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    public void ImportCsv(string csv)
    {
        var (rules, problems) = ReplaceRulesCsv.Parse(csv);
        if (rules.Count > 0)
        {
            Edit(list => list.AddRange(rules));
        }

        ImportNote = problems.Count == 0
            ? UiText.Format(Strings.SettingsRulesImported, rules.Count)
            : UiText.Format(Strings.SettingsRulesImportProblems, rules.Count, string.Join(" ", problems));
    }

    /// <summary>The CSV file could not be read (the view's file picker reports it here).</summary>
    public void ShowImportError(string message) =>
        ImportNote = UiText.Format(Strings.SettingsRulesImportFailed, message);

    internal void Add(RuleRow draft) => Edit(list => list.Add(draft.ToRule()));

    internal void Set(int index, ReplaceRule rule) => Edit(list =>
    {
        if (index < list.Count)
        {
            list[index] = rule;
        }
    });

    internal void RemoveAt(int index) => Edit(list =>
    {
        if (index < list.Count)
        {
            list.RemoveAt(index);
        }
    });

    private void Edit(Action<List<ReplaceRule>> change)
    {
        if (_id is not { } id)
        {
            return;
        }

        session.Edit(c =>
        {
            if (c.Processors.GetValueOrDefault(id) is ReplaceProcessorConfig replace)
            {
                change(replace.Rules);
            }

            return c;
        });
    }
}
