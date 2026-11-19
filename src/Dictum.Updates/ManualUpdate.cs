namespace Dictum.Updates;

/// <summary>What a person does by hand when Dictum does not update its installation itself.</summary>
public static class ManualUpdate
{
    /// <summary>The commands that build and install the new Arch package from the release's PKGBUILD, one per line.</summary>
    public static string PackageManagerCommands(ReleaseVersion version) => string.Join('\n',
        $"mkdir -p ~/dictum-{version} && cd ~/dictum-{version}",
        $"curl -LO {ReleaseFiles.PkgbuildUrl(version).AbsoluteUri}",
        "makepkg -si");
}
