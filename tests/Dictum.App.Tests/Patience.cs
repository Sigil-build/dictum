namespace Dictum.App.Tests;

/// <summary>How long a test waits for another thread or process before it fails.</summary>
internal static class Patience
{
    /// <summary>The 10 s of <see cref="TestTimeout.Default"/>, which <see cref="Eventually"/> waits by default too.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMilliseconds(TestTimeout.Default);
}
