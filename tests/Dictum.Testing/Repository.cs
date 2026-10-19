namespace Dictum.Testing;

/// <summary>The repository the tests run from: the folder above the test output that holds <c>Dictum.slnx</c>.</summary>
public static class Repository
{
    private const string SolutionFile = "Dictum.slnx";

    public static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, SolutionFile)))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException($"{SolutionFile} not found above {AppContext.BaseDirectory}");
        }
    }
}
