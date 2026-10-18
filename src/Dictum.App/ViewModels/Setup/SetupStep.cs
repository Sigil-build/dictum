using CommunityToolkit.Mvvm.ComponentModel;

namespace Dictum.App.ViewModels.Setup;

/// <summary>One step of the setup wizard (spec 8): its name for the step line, and whether Next may leave it.</summary>
public abstract partial class SetupStep : ViewModelBase
{
    public abstract string Name { get; }

    [ObservableProperty]
    public partial bool CanContinue { get; protected set; } = true;

    /// <summary>The step is on screen.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>The step was left, or the window closed.</summary>
    public virtual void OnHidden()
    {
    }
}
