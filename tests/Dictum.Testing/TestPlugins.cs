using System.Text.Json.Nodes;
using Dictum.Plugins;
using Dictum.TestPlugin;

namespace Dictum.Testing;

/// <summary>Plugin folders made from the built test plugin (<c>tests/Dictum.TestPlugin</c>), for tests that launch it.</summary>
public static class TestPlugins
{
    private const string ProgramName = "dictum-test-plugin";
    private const string BehaviourFile = "behaviour.json";

    /// <summary>The test plugin that the project reference built, in the same configuration as this test.</summary>
    public static string BinDirectory
    {
        get
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            return Path.Combine(Repository.Root, "tests", "Dictum.TestPlugin", "bin", configuration, "net10.0");
        }
    }

    private static string ProgramFile => OperatingSystem.IsWindows() ? ProgramName + ".exe" : ProgramName;

    /// <summary>
    /// A copy of the test plugin in <paramref name="dir"/>, under <paramref name="folderName"/> (else the id), with a
    /// <c>plugin.json</c> for this system and, when given, the behaviour as <c>behaviour.json</c>.
    /// </summary>
    public static string Folder(TempDirectory dir, string id = "test", string? folderName = null, TestPluginBehaviour? behaviour = null)
    {
        var folder = Path.Combine(dir.Path, folderName ?? id);
        CopyDirectory(BinDirectory, folder);

        var manifest = new JsonObject
        {
            ["protocol"] = PluginProtocol.Version,
            ["id"] = id,
            ["name"] = "Test",
            ["version"] = "1.0.0",
            ["executable"] = new JsonObject { [PluginProtocol.CurrentRid] = ProgramFile },
        };
        File.WriteAllText(Path.Combine(folder, PluginProtocol.ManifestFile), manifest.ToJsonString());
        behaviour?.Save(Path.Combine(folder, BehaviourFile));

        if (!OperatingSystem.IsWindows())
        {
            var program = Path.Combine(folder, ProgramFile);
            File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return folder;
    }

    /// <summary>A behaviour file to pass as the <c>--model</c>, which the test plugin prefers to <c>behaviour.json</c>.</summary>
    public static string Model(TempDirectory dir, TestPluginBehaviour behaviour, string name = "model.json")
    {
        var path = dir.File(name);
        behaviour.Save(path);
        return path;
    }

    /// <summary>A launch of the plugin in <paramref name="folder"/>, its name, version and program read from its manifest.</summary>
    public static PluginLaunch Launch(string folder, string? model = null, int? threads = null, string id = "test")
    {
        var manifest = PluginManifest.Load(folder);
        return new PluginLaunch(id, manifest.Name, manifest.Version, folder, manifest.ExecutablePath(folder), model, threads);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }
}
