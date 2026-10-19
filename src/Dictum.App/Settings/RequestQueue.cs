namespace Dictum.App.Settings;

/// <summary>
/// Daemon requests sent one after another. Each request opens its own connection, so two sent at once could reach the
/// daemon in either order.
/// </summary>
internal sealed class RequestQueue
{
    private Task _last = Task.CompletedTask;

    /// <summary>Starts <paramref name="step"/> once every step queued before it has finished.</summary>
    public void Enqueue(Func<Task> step) => _last = After(_last, step);

    private static async Task After(Task previous, Func<Task> step)
    {
        await previous;
        await step();
    }
}
