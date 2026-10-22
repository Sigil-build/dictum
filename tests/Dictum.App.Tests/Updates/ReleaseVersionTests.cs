using Dictum.App.Updates;

namespace Dictum.App.Tests.Updates;

public sealed class ReleaseVersionTests
{
    private static readonly ReleaseVersion Current = new(0, 4, 0);

    [Theory]
    [InlineData("v0.4.1")]
    [InlineData("v0.5.0")]
    [InlineData("v1.0.0")]
    [InlineData("0.10.0")]
    public void ALaterTagIsNewer(string tag) => Assert.True(ReleaseVersion.Parse(tag) > Current);

    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("0.4.0")]
    [InlineData("V0.4.0")]
    public void TheSameVersionIsNotNewer(string tag)
    {
        Assert.Equal(Current, ReleaseVersion.Parse(tag));
        Assert.False(ReleaseVersion.Parse(tag) > Current);
    }

    [Theory]
    [InlineData("v0.3.9")]
    [InlineData("v0.1.0")]
    public void AnEarlierTagIsOlder(string tag) => Assert.True(ReleaseVersion.Parse(tag) < Current);

    // GitHub's latest release skips the ones marked as pre-releases, but a tag can still carry a suffix: Dictum compares
    // X.Y.Z only, so it does not offer one.
    [Theory]
    [InlineData("v0.5.0-rc.1")]
    [InlineData("v0.5.0-beta")]
    [InlineData("v0.5.0+build.7")]
    public void APreReleaseTagIsIgnored(string tag) => Assert.Null(ReleaseVersion.Parse(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("v0.5")]
    [InlineData("v0.5.0.1")]
    [InlineData("v0.5.x")]
    [InlineData("v0.-5.0")]
    [InlineData(" v0.5.0")]
    [InlineData("v0.5.99999999999")]
    public void AMalformedTagIsIgnored(string? tag) => Assert.Null(ReleaseVersion.Parse(tag));

    [Fact]
    public void ItReadsAsTheRailShowsAVersion() => Assert.Equal("0.10.2", new ReleaseVersion(0, 10, 2).ToString());

    // The app's own version: VersionPrefix, without what a build adds after it.
    [Theory]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("0.4.0+3f2a9c1", "0.4.0")]
    [InlineData("0.5.0-dev", "0.5.0")]
    public void TheAppsVersionIsItsPrefix(string informational, string expected) =>
        Assert.Equal(expected, ReleaseVersion.OfApp(informational)?.ToString());

    [Fact]
    public void AnAppVersionThatDoesNotParseIsNone() => Assert.Null(ReleaseVersion.OfApp("?"));
}
