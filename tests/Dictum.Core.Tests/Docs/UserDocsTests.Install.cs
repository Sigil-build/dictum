using Dictum.Core.Configuration;

namespace Dictum.Core.Tests.Docs;

public partial class UserDocsTests
{
    /// <summary>
    /// What the install, removal and files pages must say word for word, in English and in Ukrainian: the packages'
    /// file names, the folders they install to and keep data in, the AppImage's CLI word and the CLI's path on Windows.
    /// A renamed package or folder fails here before a reader follows a step that no longer works.
    /// </summary>
    private static readonly Dictionary<string, string[]> InstallNeedles = new(StringComparer.Ordinal)
    {
        ["getting-started.md"] =
        [
            "dictum-<version>-win-x64.msi", "dictum-<version>-win-x64.zip",
            "dictum-<version>-linux-x64.AppImage", "dictum-<version>-linux-x64.tar.gz",
            @"%LOCALAPPDATA%\Programs\Dictum", "install.sh", "makepkg -si", ".AppImage dictum ",
            "SmartScreen", "Smart App Control",
            "glibc 2.38", "xdg-desktop-portal",
            "dictum init", """& "$env:LOCALAPPDATA\Programs\Dictum\dictum.exe" doctor""",
            "not on PATH",
        ],
        ["guides/update-and-remove.md"] = ["~/.local/lib/dictum/uninstall.sh"],
        ["reference/files.md"] = [@"%APPDATA%\Dictum", @"%LOCALAPPDATA%\Dictum", "~/.config/dictum", "~/.local/share/dictum"],
    };

    [Fact]
    public void ThePagesNameWhatThePackagesInstall()
    {
        var problems = new List<string>();
        foreach (var (page, needles) in InstallNeedles)
        {
            foreach (var file in new[] { page, page[..^".md".Length] + UkrainianSuffix })
            {
                if (!File.Exists(Full(file)))
                {
                    problems.Add($"{file} does not exist");
                    continue;
                }

                var text = File.ReadAllText(Full(file));
                problems.AddRange(needles.Where(needle => !text.Contains(needle, StringComparison.Ordinal)).Select(needle => $"{file} does not mention {needle}"));
            }
        }

        AssertNone(problems);
    }

    // The files page shows a config.json for readers to copy from: it must load in this version without an error or a
    // warning, and read the same in both languages.
    [Fact]
    public void TheConfigOnTheFilesPageIsValid()
    {
        const string page = "reference/files.md";
        var examples = new[] { page, page[..^".md".Length] + UkrainianSuffix }.Select(file => (File: file, Json: JsonBlock(file))).ToList();

        foreach (var (file, json) in examples)
        {
            var config = ConfigStore.Parse(json);
            AssertNone(ConfigValidator.Validate(config).Select(issue => $"{file}: {issue}"));
        }

        Assert.Equal(examples[0].Json, examples[1].Json);
    }

    /// <summary>The page's one <c>```json</c> block, without its fences.</summary>
    private static string JsonBlock(string page)
    {
        Assert.True(File.Exists(Full(page)), $"{page} does not exist");
        var lines = Lines(Full(page));
        var starts = lines.Index().Where(line => line.Item.Trim() == "```json").Select(line => line.Index).ToList();
        Assert.True(starts.Count == 1, $"{page} needs exactly one ```json block, but has {starts.Count}.");

        var body = lines.Skip(starts[0] + 1).TakeWhile(line => line.Trim() != "```").ToList();
        var indent = body.Where(line => line.Trim().Length > 0).Min(line => line.Length - line.TrimStart().Length);
        return string.Join('\n', body.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
    }
}
