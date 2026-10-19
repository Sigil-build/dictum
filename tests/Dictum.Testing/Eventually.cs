namespace Dictum.Testing;

/// <summary>Waits for a condition that work on another thread makes true.</summary>
public static class Eventually
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <exception cref="TimeoutException">The condition is still false after <paramref name="timeout"/> (10 s by default).</exception>
    public static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}");
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }
}
