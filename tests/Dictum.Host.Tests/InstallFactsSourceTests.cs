using Dictum.Host.Updates;

namespace Dictum.Host.Tests;

/// <summary>The facts the detector gets from the real system; only the ones that hold on any machine are checked.</summary>
public sealed class InstallFactsSourceTests
{
    [Fact]
    public void TheCurrentFactsNameThisProcess()
    {
        var facts = InstallFactsSource.Current();

        Assert.Equal(Environment.ProcessPath, facts.ProcessPath);
        Assert.Equal(OperatingSystem.IsWindows(), facts.IsWindows);
        Assert.Equal(OperatingSystem.IsLinux(), facts.IsLinux);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), facts.Home);
    }

    [Fact]
    public void TheWriteProbeLeavesNothingBehind()
    {
        var folder = Directory.CreateTempSubdirectory("dictum-facts-").FullName;
        try
        {
            Assert.True(InstallFactsSource.CanWrite(folder));
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
            Assert.False(InstallFactsSource.CanWrite(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
