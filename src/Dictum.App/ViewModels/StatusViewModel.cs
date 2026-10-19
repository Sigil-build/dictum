using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Daemon;
using Dictum.App.Resources;

namespace Dictum.App.ViewModels;

/// <summary>The daemon link's state and problem, the profiles and the file paths: the status the System page shows.</summary>
public sealed partial class StatusViewModel(string configPath, string logPath, Action restart) : ViewModelBase
{
    [ObservableProperty]
    public partial string Headline { get; set; } = Strings.StatusStarting;

    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    public partial bool CanRestart { get; set; }

    /// <summary>The desktop has no tray: the window is the only way to see Dictum's state.</summary>
    [ObservableProperty]
    public partial bool NoTray { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Profiles { get; set; } = [];

    public string ConfigLine { get; } = string.Format(CultureInfo.CurrentCulture, Strings.StatusConfigFile, configPath);

    public string LogLine { get; } = string.Format(CultureInfo.CurrentCulture, Strings.StatusLogFile, logPath);

    public void OnLink(DaemonLink link)
    {
        Headline = link.State switch
        {
            DaemonLinkState.Connected => string.Format(CultureInfo.CurrentCulture, Strings.StatusConnected, link.Status?.ProcessId),
            DaemonLinkState.Restarting => Strings.StatusRestarting,
            DaemonLinkState.Failed => Strings.StatusFailed,
            DaemonLinkState.VersionMismatch => Strings.StatusVersionMismatch,
            DaemonLinkState.NotConfigured => Strings.StatusNotConfigured,
            DaemonLinkState.Stopped => Strings.StatusStopped,
            _ => Strings.StatusStarting,
        };
        Problem = link.Problem;
        CanRestart = link.State is DaemonLinkState.Failed or DaemonLinkState.VersionMismatch;
        Profiles = link.State == DaemonLinkState.Connected && link.Status is { } status
            ? [.. status.Profiles.Select(p => p.Hotkey is null ? p.Id : $"{p.Id} · {p.Hotkey}")]
            : [];
    }

    [RelayCommand]
    private void Restart() => restart();
}
