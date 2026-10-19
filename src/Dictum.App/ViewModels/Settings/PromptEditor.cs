using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Processing;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// The prompt of the LLM editor: the prompt files to choose from and the chosen one's text, written to
/// <c>prompts/&lt;name&gt;.md</c> 500 ms after the last keystroke. A built-in prompt can be restored, and the text can
/// become a new prompt.
/// </summary>
public sealed partial class PromptEditor : ObservableObject
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly PromptLibrary _prompts;
    private readonly ITimers _timers;
    private readonly Action<string> _choose;
    private readonly Action _changed;
    private string? _loaded;
    private bool _loading;
    private IDisposable? _save;
    private (string Name, string Text)? _pending;

    /// <param name="choose">Makes the processor use a prompt.</param>
    /// <param name="changed">The text the processor would use changed: the preview runs again.</param>
    public PromptEditor(PromptLibrary prompts, ITimers timers, Action<string> choose, Action changed)
    {
        _prompts = prompts;
        _timers = timers;
        _choose = choose;
        _changed = changed;
    }

    [ObservableProperty]
    public partial IReadOnlyList<string> Names { get; private set; } = [];

    [ObservableProperty]
    public partial string? Selected { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string? Note { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The other LLM processors that use this prompt, whom an edit here changes too; null when none.</summary>
    [ObservableProperty]
    public partial string? Shared { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool CanRestore { get; private set; }

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string? NewNameError { get; private set; }

    /// <summary>Shows prompt <paramref name="name"/>; its text is read again only when another prompt was shown.</summary>
    /// <param name="sharers">The other processors that use the prompt.</param>
    public void Load(string name, IReadOnlyList<string> sharers)
    {
        _loading = true;
        try
        {
            var names = _prompts.Names();
            if (!names.Contains(name))
            {
                names = [.. names, name];
            }

            if (!names.SequenceEqual(Names))
            {
                Names = names;
            }

            Selected = name;
            if (_loaded != name)
            {
                _loaded = name;
                Text = Read(name);
            }

            ShowState(name);
            Shared = sharers.Count == 0 ? null : UiText.Format(Strings.SettingsLlmPromptShared, string.Join(", ", sharers));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Another processor is shown: the next <see cref="Load"/> reads its prompt's text.</summary>
    public void Forget() => _loaded = null;

    /// <summary>Writes a pending edit now: the page is leaving, another processor is chosen, or a preview runs.</summary>
    public void Flush()
    {
        if (_pending is null)
        {
            return;
        }

        _save?.Dispose();
        _save = null;
        Save();
    }

    partial void OnSelectedChanged(string? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        Flush();
        _choose(value);
    }

    partial void OnTextChanged(string value)
    {
        if (_loading || Selected is not { } name)
        {
            return;
        }

        _save?.Dispose();
        _pending = (name, value);
        _save = _timers.Once(SaveDelay, Save);
        _changed();
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        if (Selected is not { } name)
        {
            return;
        }

        try
        {
            _prompts.Delete(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another program holds the file; the edit in progress is kept.
            Error = UiText.Format(Strings.SettingsLlmPromptRestoreFailed, ex.Message);
            return;
        }

        _save?.Dispose();
        _save = null;
        _pending = null;
        _loading = true;
        Text = Read(name);
        _loading = false;
        ShowState(name);
        _changed();
    }

    [RelayCommand]
    private void Create()
    {
        var name = NewName.Trim();
        if (!PromptLibrary.IsValidName(name))
        {
            NewNameError = Strings.SettingsLlmPromptNameInvalid;
            return;
        }

        // Windows file names ignore case: "Cleanup" would replace the built-in cleanup.
        if (_prompts.Names().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            NewNameError = Strings.SettingsLlmPromptNameTaken;
            return;
        }

        // The text being edited becomes the new prompt; the old prompt keeps what it had on disk.
        _save?.Dispose();
        _save = null;
        _pending = null;
        try
        {
            _prompts.Write(name, Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NewNameError = UiText.Format(Strings.SettingsLlmPromptSaveFailed, ex.Message);
            return;
        }

        NewNameError = null;
        NewName = "";
        _loaded = name;
        _choose(name);
    }

    /// <summary>The prompt's text; a file another program holds is shown as an error, not thrown.</summary>
    private string Read(string name)
    {
        try
        {
            var text = _prompts.Read(name);
            Error = null;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = UiText.Format(Strings.SettingsLlmPromptReadFailed, ex.Message);
            return "";
        }
    }

    private void Save()
    {
        _save = null;
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        try
        {
            _prompts.Write(pending.Name, pending.Text);
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = UiText.Format(Strings.SettingsLlmPromptSaveFailed, ex.Message);
        }

        if (pending.Name == Selected)
        {
            ShowState(pending.Name);
        }
    }

    private void ShowState(string name)
    {
        CanRestore = PromptLibrary.IsBuiltIn(name) && _prompts.HasFile(name);
        Note = UiText.Format(
            !PromptLibrary.IsBuiltIn(name) ? Strings.SettingsLlmPromptFile
            : _prompts.HasFile(name) ? Strings.SettingsLlmPromptOverride
            : Strings.SettingsLlmPromptBuiltIn,
            name);
    }
}
