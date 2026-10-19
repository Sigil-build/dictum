using Dictum.Core;

namespace Dictum.Setup;

public static class ModelFiles
{
    /// <summary>The file is in the models directory with the catalog's size; its hash was checked when it was downloaded.</summary>
    public static bool IsInstalled(AppPaths paths, CatalogEntry entry)
    {
        var path = paths.ResolveModel(entry.File);
        return File.Exists(path) && new FileInfo(path).Length == entry.Size;
    }
}
