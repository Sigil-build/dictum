using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dictum.Testing;

namespace Dictum.Core.Tests;

/// <summary>The layering of CLAUDE.md's Layout and its interop and AOT rules, checked on the source.</summary>
public sealed partial class ArchitectureTests
{
    private const string Core = "Dictum.Core";
    private const string Audio = "Dictum.Audio";
    private const string Processing = "Dictum.Processing";
    private const string Setup = "Dictum.Setup";
    private const string SttWhisper = "Dictum.Stt.Whisper";
    private const string SttOpenAi = "Dictum.Stt.OpenAi";
    private const string Platform = "Dictum.Platform";
    private const string PlatformWindows = "Dictum.Platform.Windows";
    private const string PlatformLinux = "Dictum.Platform.Linux";
    private const string Host = "Dictum.Host";
    private const string App = "Dictum.App";
    private const string Cli = "Dictum.Cli";

    /// <summary>What each project may reference. A new project fails the test until it has a place here.</summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        [Core] = [],
        [Audio] = [Core],
        [Processing] = [Core],
        [Setup] = [Core],
        [SttWhisper] = [Core, Audio],
        [SttOpenAi] = [Core, Audio],
        [Platform] = [Core],
        [PlatformWindows] = [Platform],
        [PlatformLinux] = [Platform],
        [Host] = [Core, Audio, Processing, Setup, SttWhisper, SttOpenAi, Platform, PlatformWindows, PlatformLinux],
        [App] = [Host],
        [Cli] = [Host],
    };

    /// <summary>The projects that may call native code or the Windows registry.</summary>
    private static readonly string[] InteropProjects = [Platform, PlatformWindows, PlatformLinux, Audio, SttWhisper];

    /// <summary>The executables; libraries stay AOT-compatible without publishing.</summary>
    private static readonly string[] AotProjects = [App, Cli];

    /// <summary>The PortAudio implementation, which Audio owns and only Host, the composition root, may name.</summary>
    private static readonly string[] PortAudioProjects = [Audio, Host];

    private static string Src => Path.Combine(Repository.Root, "src");

    [Fact]
    public void EachProjectReferencesOnlyWhatItsLayerAllows()
    {
        var offenders = new List<string>();
        foreach (var project in Projects())
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (!AllowedReferences.TryGetValue(name, out var allowed))
            {
                offenders.Add($"{Relative(project)}: a project the layering does not know");
                continue;
            }

            offenders.AddRange(References(project)
                .Where(reference => !allowed.Contains(reference))
                .Select(reference => $"{Relative(project)}: references {reference}"));
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void MacOsHasNoProject() =>
        Assert.DoesNotContain(Projects(), project => Path.GetFileNameWithoutExtension(project).Contains("MacOS", StringComparison.Ordinal));

    [Fact]
    public void NativeCodeAndTheRegistryStayInThePlatformAudioAndWhisperProjects()
    {
        var offenders = Offenders(ProjectNames().Except(InteropProjects), InteropPattern());

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NativeCallsUseLibraryImport()
    {
        var offenders = Offenders(ProjectNames(), DllImportPattern());

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Plain .NET <c>OperatingSystem</c> branches are fine in Core; interop is not.</summary>
    [Fact]
    public void CoreHasNoInterop()
    {
        var offenders = Offenders([Core], CoreInteropPattern());

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void OnlyTheExecutablesPublishAot()
    {
        var root = Repository.Root;
        string[] shared =
        [
            Path.Combine(root, "Directory.Build.props"),
            Path.Combine(root, "Directory.Build.targets"),
            Path.Combine(root, "tests", "Directory.Build.props"),
        ];
        var files = Projects()
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
            .Concat(shared.Where(File.Exists));

        var offenders = files
            .Where(file => PublishAotPattern().IsMatch(File.ReadAllText(file)))
            .Where(file => !AotProjects.Contains(Path.GetFileNameWithoutExtension(file)) || !file.StartsWith(Src, StringComparison.Ordinal))
            .Select(Relative)
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void OnlyHostNamesThePortAudioImplementation()
    {
        var offenders = Offenders(ProjectNames().Except(PortAudioProjects), PortAudioPattern());

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Every project file under src, one folder deep.</summary>
    private static List<string> Projects() =>
        [.. Directory.EnumerateDirectories(Src).SelectMany(dir => Directory.EnumerateFiles(dir, "*.csproj"))];

    private static List<string> ProjectNames() => [.. Projects().Select(project => Path.GetFileNameWithoutExtension(project))];

    private static IEnumerable<string> References(string project) =>
        XDocument.Load(project).Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!));

    /// <summary>Each code line of the projects' C# files that matches <paramref name="pattern"/>, as <c>file:line: text</c>.</summary>
    private static List<string> Offenders(IEnumerable<string> projects, Regex pattern)
    {
        var offenders = new List<string>();
        foreach (var project in projects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Src, project), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (!line.StartsWith("//", StringComparison.Ordinal) && pattern.IsMatch(line))
                    {
                        offenders.Add($"{Relative(file)}:{i + 1}: {line}");
                    }
                }
            }
        }

        return offenders;
    }

    private static string Relative(string path) => Path.GetRelativePath(Repository.Root, path);

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    [GeneratedRegex(@"\b(?:LibraryImport|DllImport)\b|\bMicrosoft\.Win32\b|\bRegistry(?:Key|ValueKind|View|Hive)?\b")]
    private static partial Regex InteropPattern();

    [GeneratedRegex(@"\bDllImport\b")]
    private static partial Regex DllImportPattern();

    [GeneratedRegex(@"\b(?:LibraryImport|DllImport|NativeLibrary|Marshal)\b|\bSystem\.Runtime\.InteropServices\b|\bMicrosoft\.Win32\b|\bunsafe\b|\bextern\b")]
    private static partial Regex CoreInteropPattern();

    [GeneratedRegex(@"<PublishAot>\s*true\s*</PublishAot>", RegexOptions.IgnoreCase)]
    private static partial Regex PublishAotPattern();

    [GeneratedRegex(@"\bDictum\.Audio\.PortAudio\b")]
    private static partial Regex PortAudioPattern();
}
