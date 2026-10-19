using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Configuration;

public sealed class DictumConfigTests
{
    [Fact]
    public void FindProfileFindsTheIdAsWritten()
    {
        var config = new DictumConfig
        {
            Profiles =
            {
                new ProfileConfig { Id = "work", Stt = "local" },
                new ProfileConfig { Id = "home", Stt = "local" },
            },
        };

        Assert.Same(config.Profiles[1], config.FindProfile("home"));
        Assert.Null(config.FindProfile("Home"));
        Assert.Null(config.FindProfile(null));
    }
}
