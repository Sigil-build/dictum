using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public sealed partial class ProcessorsView : UserControl
{
    public ProcessorsView()
    {
        InitializeComponent();
        ImportButton.Click += async (_, _) => await ImportAsync();
        Pickers.ClearAfterPick(this);

        // A pick in "Add to profile…" hides the warning and, with it, the picker that had the focus; the focus moves on to
        // the editor's first field, where Tab from the picker would have gone. The pick is the picker's SelectedItem only
        // for a moment, as the list it came from empties at once, so SelectionChanged never reports it.
        AddToPicker.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectingItemsControl.SelectedItemProperty && e.NewValue is not null)
            {
                Dispatcher.UIThread.Post(FocusEditor, DispatcherPriority.Loaded);
            }
        };
    }

    private void FocusEditor()
    {
        if (AddToPicker.IsEffectivelyVisible || DataContext is not ProcessorsPage page)
        {
            return;
        }

        var first = page.IsLlm ? UrlBox : RuleList.ContainerFromIndex(0)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        first?.Focus();
    }

    private async Task ImportAsync()
    {
        if (DataContext is not ProcessorsPage page || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.SettingsRulesImport,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Strings.SettingsRulesCsvFiles) { Patterns = ["*.csv", "*.txt"] }],
            });
            if (files.Count != 1)
            {
                return;
            }

            await using var stream = await files[0].OpenReadAsync();
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            page.Rules.ImportCsv(ReplaceRulesCsv.Decode(bytes.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            page.Rules.ShowImportError(ex.Message);
        }
    }
}
