using CommunityToolkit.Mvvm.ComponentModel;
using Dictum.App.Resources;

namespace Dictum.App.ViewModels.Setup;

/// <summary>Step 5: start <c>dictum-gui</c> at login, Yes (recommended) or No; written after the config is saved.</summary>
public sealed partial class SetupLoginStep : SetupStep
{
    /// <param name="available">This system has a login item Dictum can write, and the app knows its own path.</param>
    public SetupLoginStep(bool available, bool startAtLogin)
    {
        IsAvailable = available;
        StartAtLogin = available && startAtLogin;
    }

    public override string Name => Strings.SetupStepLogin;

    public bool IsAvailable { get; }

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    /// <summary>The "No" radio; always the opposite of <see cref="StartAtLogin"/>.</summary>
    [ObservableProperty]
    public partial bool StartManually { get; set; } = true;

    partial void OnStartAtLoginChanged(bool value) => StartManually = !value;

    partial void OnStartManuallyChanged(bool value) => StartAtLogin = !value;
}
