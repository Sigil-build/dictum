using Dictum.Updates;

namespace Dictum.Core.Tests.Updates;

public sealed class ManualUpdateTests
{
    [Fact]
    public void PackageManagerCommandsNameTheVersionsPkgbuild() =>
        Assert.Equal(
            "mkdir -p ~/dictum-0.7.1 && cd ~/dictum-0.7.1\n"
            + "curl -LO https://github.com/Sigil-build/dictum/releases/download/v0.7.1/PKGBUILD\n"
            + "makepkg -si",
            ManualUpdate.PackageManagerCommands(new ReleaseVersion(0, 7, 1)));
}
