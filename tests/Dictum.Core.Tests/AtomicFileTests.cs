namespace Dictum.Core.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void ReplacesTheFileWithUtf8WithoutAMarkAndLeavesNoTemporaryFile()
    {
        using var dir = new TempDirectory();
        var path = dir.File("setup.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "новий");

        Assert.Equal("новий"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(dir.Path));
    }
}
