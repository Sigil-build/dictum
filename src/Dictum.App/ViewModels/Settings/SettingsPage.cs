using Dictum.App.Daemon;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// A page of the Settings window. It is told when it shows and hides and when the daemon link changes, and may open a
/// sub-page or go back. Everything runs on the UI thread.
/// </summary>
public abstract class SettingsPage : ViewModelBase
{
    public abstract string Title { get; }

    /// <summary>
    /// The page's section of the Settings reference, which the window's Help opens: one of
    /// <see cref="Dictum.Core.Docs.DocsAnchors.Settings"/>.
    /// </summary>
    public abstract string DocsAnchor { get; }

    public event Action<SettingsPage>? NavigateRequested;

    public event Action? BackRequested;

    public virtual void OnShown()
    {
    }

    /// <summary>The page was left, or its window closed.</summary>
    public virtual void OnHidden()
    {
    }

    /// <summary>The daemon connected, dropped or restarted, whether the page is shown or not.</summary>
    public virtual void OnLink(DaemonLink link)
    {
    }

    protected void Navigate(SettingsPage page) => NavigateRequested?.Invoke(page);

    protected void GoBack() => BackRequested?.Invoke();
}
