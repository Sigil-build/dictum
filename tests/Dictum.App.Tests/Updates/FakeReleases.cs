using Dictum.App.Updates;

namespace Dictum.App.Tests.Updates;

/// <summary>GitHub's latest release as the test sets it; counts the requests. Never the real API.</summary>
internal sealed class FakeReleases : IReleaseSource
{
    public int Requests { get; private set; }

    /// <summary>What the next request gets; the app's own version until a test says otherwise.</summary>
    public ReleaseLookup Next { get; set; } = Found("v0.4.0");

    /// <summary>An answer that comes when the test completes it.</summary>
    public Task<ReleaseLookup>? Pending { get; set; }

    public Exception? Throws { get; set; }

    public static ReleaseLookup Found(string tag) => new(new LatestRelease(tag, Page(tag)));

    public static Uri Page(string tag) => new($"https://github.com/Sigil-build/dictum/releases/tag/{tag}");

    public Task<ReleaseLookup> LatestAsync(CancellationToken ct)
    {
        Requests++;
        if (Throws is { } exception)
        {
            throw exception;
        }

        return Pending ?? Task.FromResult(Next);
    }
}
