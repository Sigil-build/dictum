using Avalonia.Threading;

namespace Dictum.App.Settings;

/// <summary>One-shot and repeating timers; <see cref="UiTimers"/> in the app, a manual fake in tests.</summary>
public interface ITimers
{
    IDisposable Once(TimeSpan delay, Action action);

    IDisposable Every(TimeSpan interval, Action action);
}

/// <summary>Dispatcher timers: create and dispose them on the UI thread.</summary>
public sealed class UiTimers : ITimers
{
    public static readonly UiTimers Instance = new();

    public IDisposable Once(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);

    public IDisposable Every(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => action();
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
