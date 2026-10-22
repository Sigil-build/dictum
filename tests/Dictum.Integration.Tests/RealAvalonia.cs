using Avalonia;
using Avalonia.Threading;

namespace Dictum.Integration.Tests;

/// <summary>
/// Avalonia on this OS's real platform, with Dictum's <see cref="App.App"/> and so its themes and resources. A process can set
/// Avalonia up only once, so the first test starts it on an STA thread of its own, which then runs the dispatcher for
/// every test that follows.
/// </summary>
internal static class RealAvalonia
{
    private static readonly Lazy<Task> Started = new(Start);

    /// <summary>Runs <paramref name="work"/> on Avalonia's UI thread; awaits inside it return to that thread.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        await Started.Value;
        return await Dispatcher.UIThread.InvokeAsync(work);
    }

    /// <inheritdoc cref="RunAsync{T}(Func{Task{T}})"/>
    public static Task<T> RunAsync<T>(Func<T> work) => RunAsync(() => Task.FromResult(work()));

    /// <summary>Lets the dispatcher run what is queued above background priority, such as layout and rendering.</summary>
    public static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static Task Start()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App.App>().UsePlatformDetect().SetupWithoutStarting();
            }
            catch (Exception ex)
            {
                started.SetException(ex);
                return;
            }

            started.SetResult();
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        {
            IsBackground = true,
            Name = "Avalonia UI thread",
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        return started.Task;
    }
}
