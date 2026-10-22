using Dictum.App.Daemon;
using Dictum.Core.Configuration;
using Dictum.Host.Ipc;

namespace Dictum.App.Tray;

/// <summary>What the tray asks the app to do. Calls may arrive on any thread (the Linux tray raises clicks off the UI thread).</summary>
public interface ITrayCommands
{
    void UseProfile(string profile);

    void PauseHotkeys();

    void ResumeHotkeys();

    void ShowSettings();

    void ShowHistory();

    /// <summary>Opens the documentation's overview in the browser.</summary>
    void ShowDocumentation();

    /// <summary>Opens Settings at About Dictum.</summary>
    void ShowAbout();

    void RestartDaemon();

    void Quit();
}

/// <summary>
/// Feeds <see cref="TrayState"/>, shows its view on the presenter once there is one, and turns clicks into
/// <see cref="ITrayCommands"/>. Its <c>On…</c> methods and <see cref="Tick"/> run on the UI thread.
/// </summary>
public sealed class TrayController(TrayState state, ITrayCommands commands, TimeProvider time)
{
    private ITrayPresenter? _presenter;
    private DateTimeOffset? _redrawAt;

    public TrayView View { get; private set; } = state.View();

    public void Attach(ITrayPresenter presenter)
    {
        _presenter = presenter;
        presenter.ItemClicked += item => Run(item.Command, item.Profile);
        presenter.Activated += () => Run(View.Activate, null);
        Redraw();
    }

    public void OnLink(DaemonLink link)
    {
        state.OnLink(link);
        Redraw();
    }

    public void OnEvent(IpcEvent message)
    {
        // Frames come 50 times a second and change nothing in the tray.
        if (message.Event == IpcEventKind.Frame)
        {
            return;
        }

        state.OnEvent(message);
        Redraw();
    }

    public void OnConfig(DictumConfig? config)
    {
        state.OnConfig(config);
        Redraw();
    }

    public void OnDownloads(double? progress)
    {
        state.OnDownloads(progress);
        Redraw();
    }

    /// <summary>Called every second; redraws once when a held error has expired.</summary>
    public void Tick()
    {
        if (_redrawAt is { } at && at <= time.GetUtcNow())
        {
            Redraw();
        }
    }

    private void Redraw()
    {
        View = state.View();
        _presenter?.Show(View);
        _redrawAt = state.NextChange is { } next && next > time.GetUtcNow() ? next : null;
    }

    private void Run(TrayCommand command, string? profile)
    {
        switch (command)
        {
            case TrayCommand.UseProfile when profile is not null:
                commands.UseProfile(profile);
                break;
            case TrayCommand.PauseHotkeys:
                commands.PauseHotkeys();
                break;
            case TrayCommand.ResumeHotkeys:
                commands.ResumeHotkeys();
                break;
            case TrayCommand.History:
                commands.ShowHistory();
                break;
            case TrayCommand.Settings:
                commands.ShowSettings();
                break;
            case TrayCommand.Documentation:
                commands.ShowDocumentation();
                break;
            case TrayCommand.About:
                commands.ShowAbout();
                break;
            case TrayCommand.RestartDaemon:
                commands.RestartDaemon();
                break;
            case TrayCommand.Quit:
                commands.Quit();
                break;
        }
    }
}
