namespace Dictum.Core.Tests;

/// <summary>The repository the tests run from: the folder above the test output that holds <c>Dictum.slnx</c>.</summary>
internal static class Repository
{
    public static string Root
    {
        get
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
}
