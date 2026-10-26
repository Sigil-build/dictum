using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dictum.App.Resources;
using Dictum.App.ViewModels.Setup;

namespace Dictum.App.Views.Setup;

public sealed partial class SetupView : UserControl
{
    private const string MakeMainClass = "makeMain";
    private const string PickRowClass = "pickRow";
    private const string ChangeFolderClass = "changeFolder";

    public SetupView()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, OnClick, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// "Make main" hides once its language is the main one, so the focus stays in the row, on its box. Step 6's Change…
    /// opens the folder picker.
    /// </summary>
    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button button && button.Classes.Contains(MakeMainClass)
            && button.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(PickRowClass)) is { } row)
        {
            Dispatcher.UIThread.Post(() => row.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault()?.Focus(), DispatcherPriority.Loaded);
        }

        if (e.Source is Button { DataContext: SetupDownloadsStep step } change && change.Classes.Contains(ChangeFolderClass))
        {
            _ = ChooseFolderAsync(step);
        }
    }

    /// <summary>The OS folder picker; a folder without a local path (a portal's virtual one) is not offered as a choice.</summary>
    private async Task ChooseFolderAsync(SetupDownloadsStep step)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
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
            step.ChooseFolder(path);
        }
    }

    /// <summary>
    /// Step 8's try-it box takes the focus when it appears: the daemon pastes into the focused control, so the first
    /// dictation lands in the box instead of on the Finish button.
    /// </summary>
    private void FocusTryBox(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }
    }
}
