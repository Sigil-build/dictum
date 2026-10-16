namespace Dictum.App.Tests;

internal static class Eventually
{
    public static async Task WaitForAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
