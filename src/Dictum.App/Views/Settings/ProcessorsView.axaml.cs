using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dictum.App.Resources;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public partial class ProcessorsView : UserControl
{
    public ProcessorsView()
    {
        InitializeComponent();
        ImportButton.Click += async (_, _) => await ImportAsync();

        // Leaving a key field commits it, as Enter does (4b).
        AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox box && box.Classes.Contains("apiKey") && DataContext is ProcessorsPage page)
            {
                page.Llm.CommitKeyCommand.Execute(null);
            }
        }, RoutingStrategies.Bubble);
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
            using var reader = new StreamReader(stream);
            page.Rules.ImportCsv(await reader.ReadToEndAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            page.Rules.ShowImportError(ex.Message);
        }
    }
}
