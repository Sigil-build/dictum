namespace Dictum.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyIsReferenced() =>
        Assert.Equal("Dictum.Core", typeof(AssemblyMarker).Assembly.GetName().Name);
}
