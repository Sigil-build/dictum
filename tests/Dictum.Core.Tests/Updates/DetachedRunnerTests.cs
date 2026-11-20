using Dictum.Updates.Install;

namespace Dictum.Core.Tests.Updates;

/// <summary>Where the Linux runner's setsid comes from: never a bare name for the system to look up.</summary>
public sealed class DetachedRunnerTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "dictum-bin");

    private static string OnPath(params string[] folders) => string.Join(Path.PathSeparator, folders);

    [Fact]
    public void UtilLinuxsPlacesComeBeforePath()
    {
        var found = DetachedRunner.FindSetsid(OnPath(Folder), _ => true);

        Assert.Equal("/usr/bin/setsid", found);
    }

    [Fact]
    public void BinIsTriedWhenUsrBinHasNone()
    {
        var found = DetachedRunner.FindSetsid(OnPath(Folder), path => path != "/usr/bin/setsid");

        Assert.Equal("/bin/setsid", found);
    }

    [Fact]
    public void ASetsidElsewhereIsFoundThroughPathByItsFullPath()
    {
        var found = DetachedRunner.FindSetsid(OnPath(Folder), path => path.StartsWith(Folder, StringComparison.Ordinal));

        Assert.Equal(Path.Combine(Folder, "setsid"), found);
    }

    [Fact]
    public void ARelativeFolderOnPathIsNotSearched()
    {
        var tried = new List<string>();

        var found = DetachedRunner.FindSetsid(OnPath("bin", ".", ""), path =>
        {
            tried.Add(path);
            return false;
        });

        Assert.Null(found);
        Assert.Equal(["/usr/bin/setsid", "/bin/setsid"], tried);
    }

    [Fact]
    public void WithoutSetsidAnywhereThereIsNone()
    {
        Assert.Null(DetachedRunner.FindSetsid(null, _ => false));
    }
}
