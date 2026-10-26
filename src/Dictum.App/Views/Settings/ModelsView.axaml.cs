using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Dictum.App.Resources;
using Dictum.App.ViewModels.Settings;

namespace Dictum.App.Views.Settings;

public sealed partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();
        Pickers.ClearAfterPick(this);
        ChangeFolderButton.Click += async (_, _) => await ChangeFolderAsync();
    }

    /// <summary>The OS folder picker; a folder without a local path (a portal's virtual one) is not offered as a choice.</summary>
    private async Task ChangeFolderAsync()
    {
        if (DataContext is not ModelsPage page || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.SettingsModelsFolderPick,
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
        {
            page.ChooseFolder(path);
        }
    }
}
