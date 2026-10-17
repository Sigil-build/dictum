using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dictum.App.ViewModels;
using Dictum.App.ViewModels.Settings;
using Dictum.App.Views;
using Dictum.App.Views.Settings;

namespace Dictum.App;

/// <summary>View for a view model, as a switch: no reflection, so Native AOT keeps every view.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        GeneralPage model => new GeneralView { DataContext = model },
        OverlayPage model => new OverlayView { DataContext = model },
        SystemPage model => new SystemView { DataContext = model },
        HistoryPage model => new HistoryView { DataContext = model },
        StatusViewModel model => new StatusView { DataContext = model },
        null => null,
        _ => new TextBlock { Text = $"No view for {param.GetType().Name}" },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
