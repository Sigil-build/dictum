using System.Globalization;

namespace Dictum.Core.Tests;

public class SourceHygieneTests
{
    /// <summary>
    /// Invisible format characters (zero-width spaces, byte-order marks inside text) cannot be seen in review and are
    /// silently dropped by some editors; in source they must be written as escapes such as <c>\uFEFF</c>.
    /// </summary>
    [Fact]
    public void SourceFilesContainNoInvisibleCharacters()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "src", "tests", "fixtures" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || Path.GetExtension(file) is not (".cs" or ".md" or ".json" or ".csproj" or ".axaml"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dictum.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Dictum.slnx not found above " + AppContext.BaseDirectory);
    }
}
