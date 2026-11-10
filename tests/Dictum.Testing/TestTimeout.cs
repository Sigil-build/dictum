namespace Dictum.Testing;

/// <summary>Milliseconds for <c>[Fact(Timeout = …)]</c>, so a test that waits on other threads fails instead of hanging.</summary>
public static class TestTimeout
{
    public const int Default = 10_000;

    /// <summary>For a test that keeps many threads, or a reader that never reads, busy on purpose.</summary>
    public const int Generous = 20_000;

    /// <summary>
    /// For a test that, on top of what <see cref="Generous"/> covers, waits out a timeout of the code under test, such as
    /// a plugin's 10 s start.
    /// </summary>
    public const int Extended = 40_000;
}
