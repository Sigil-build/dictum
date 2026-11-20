namespace Dictum.App.Updates;

/// <summary>
/// The update this desktop app runs, one at a time. Closing Settings leaves it running, so the shell keeps it and every
/// About page shows it: a page opened meanwhile offers no second update, and shows the end of this one. UI thread only.
/// </summary>
public sealed class UpdateRun
{
    private Action? _cancel;

    /// <summary>Raised on each change of the state below.</summary>
    public event Action? Changed;

    /// <summary>From Update now until it stops; it stays set once the installer took over and the app quits.</summary>
    public bool Running { get; private set; }

    /// <summary>"Downloading… 12 MB of 29 MB", "Verifying…", and so on; null while there is nothing to say.</summary>
    public string? WorkText { get; private set; }

    /// <summary>The download or the check can still be stopped.</summary>
    public bool CanCancel { get; private set; }

    /// <summary>Why the last run stopped, in the UI's language; null after a cancel and while one runs.</summary>
    public string? Problem { get; private set; }

    /// <summary>The last run stopped on a failed download, which Try again resumes.</summary>
    public bool CanRetry { get; private set; }

    /// <summary>Starts a run that <paramref name="cancel"/> stops; false while one is running already.</summary>
    public bool Start(Action cancel)
    {
        if (Running)
        {
            return false;
        }

        _cancel = cancel;
        (Running, WorkText, CanCancel, Problem, CanRetry) = (true, null, false, null, false);
        Changed?.Invoke();
        return true;
    }

    public void Show(string? workText)
    {
        WorkText = workText;
        Changed?.Invoke();
    }

    public void AllowCancel(bool allowed)
    {
        CanCancel = allowed;
        Changed?.Invoke();
    }

    public void Cancel() => _cancel?.Invoke();

    /// <summary>Ends the run: cancelled when <paramref name="problem"/> is null, failed otherwise.</summary>
    public void Stop(string? problem, bool canRetry)
    {
        _cancel = null;
        (Running, WorkText, CanCancel, Problem, CanRetry) = (false, null, false, problem, problem is not null && canRetry);
        Changed?.Invoke();
    }
}
