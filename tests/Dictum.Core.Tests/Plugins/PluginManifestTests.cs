using System.Globalization;
using Dictum.Core.Configuration;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Core.Tests.Plugins;

public sealed class PluginManifestTests : IDisposable
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDirectory _folder = new();

    /// <summary>Where a link in the plugin's folder points: outside it, as a link may.</summary>
    private readonly TempDirectory _elsewhere = new();

    private readonly FolderLinks _links = new();

    private static string Rid => PluginProtocol.CurrentRid;

    public void Dispose()
    {
        _links.Dispose();
        _folder.Dispose();
        _elsewhere.Dispose();
    }

    [Fact]
    public void AValidManifestLoads()
    {
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile()}}],"languages":["en","uk"]}]"""));

        var manifest = PluginManifest.Load(_folder.Path);

        Assert.Equal(1, manifest.Protocol);
        Assert.Equal("my-plugin", manifest.Id);
        Assert.Equal("My plugin", manifest.Name);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("Me", manifest.Publisher);
        Assert.Equal("https://example.com", manifest.Homepage);
        var model = Assert.Single(manifest.Models!);
        Assert.Equal("m", model.Id);
        Assert.Equal(["en", "uk"], model.Languages);
        Assert.True(model.SetLanguage);
        Assert.True(model.Prompt);
        Assert.Equal("a.bin", model.Files[0].File);
        Assert.Equal(ValidSha, model.Files[0].Sha256);
        Assert.Equal(42, model.Files[0].Size);
        Assert.Equal(Path.Combine(_folder.Path, "plugin.exe"), manifest.ExecutablePath(_folder.Path));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        Write(
            Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile()}}],"languages":["en"],"future":2}]""", extra: "\"future\": 1,"));

        var manifest = PluginManifest.Load(_folder.Path);

        Assert.Equal("my-plugin", manifest.Id);
    }

    [Fact]
    public void AFolderWithoutAManifestSaysSo() =>
        Assert.Equal([$"There is no plugin.json in {_folder.Path}."], Problems());

    [Fact]
    public void BrokenJsonIsReported()
    {
        File.WriteAllText(_folder.File("plugin.json"), "{ not json");

        Assert.StartsWith("plugin.json is not valid JSON:", Assert.Single(Problems()));
    }

    [Fact]
    public void ProtocolTwoIsRefused()
    {
        Write(Manifest(protocol: 2));

        Assert.Equal(["it needs protocol 2; this Dictum speaks protocol 1"], Problems());
    }

    [Theory]
    [InlineData("Bad")]
    [InlineData("whisper")]
    public void AnInvalidIdIsRefused(string id)
    {
        Write(Manifest(id: id));

        Assert.Equal([$"id '{id}' {PluginIds.Rule}"], Problems());
    }

    [Fact]
    public void NameAndVersionAreRequired()
    {
        Write(Manifest(name: "", version: " "));

        Assert.Equal(["name must not be empty", "version must not be empty"], Problems());
    }

    [Theory]
    [InlineData("name", "My\\u001b[2Jplugin", "name must not contain control or invisible formatting characters")]
    [InlineData("publisher", "Me\\r", "publisher must not contain control or invisible formatting characters")]
    [InlineData("title", "M\\u0085", "model m: title must not contain control or invisible formatting characters")]
    [InlineData("version", "1\\t0", "version must not contain control or invisible formatting characters")]
    public void ControlCharactersAreRefusedAndNeverEchoed(string field, string value, string problem)
    {
        var title = field == "title" ? value : "M";
        Write(Manifest(
            name: field == "name" ? value : "My plugin",
            version: field == "version" ? value : "1.2.0",
            publisher: field == "publisher" ? value : "Me",
            models: $$"""[{"id":"m","title":"{{title}}","files":[{{ModelFile()}}],"languages":["en"]}]"""));

        var problems = Problems();

        Assert.Equal([problem], problems);
        Assert.All(problems, p => Assert.DoesNotContain(p, IsHidden));
    }

    /// <summary>
    /// Characters a terminal draws as nothing, or as a line break: right-to-left override, zero-width space, zero-width
    /// joiner, byte order mark, line and paragraph separators. Each can reorder or split the line shown before the
    /// question whether to add the plugin.
    /// </summary>
    [Theory]
    [InlineData("\\u202e")]
    [InlineData("\\u200b")]
    [InlineData("\\u200d")]
    [InlineData("\\ufeff")]
    [InlineData("\\u2028")]
    [InlineData("\\u2029")]
    public void InvisibleFormattingCharactersAreRefusedAndNeverEchoed(string character)
    {
        Write(Manifest(name: $"My{character}plugin", id: $"my{character}plugin"));

        var problems = Problems();

        Assert.Equal(
            [$"id 'my{character}plugin' {PluginIds.Rule}", "id must not contain control or invisible formatting characters", "name must not contain control or invisible formatting characters"],
            problems);
        Assert.All(problems, p => Assert.DoesNotContain(p, IsHidden));
    }

    /// <summary>A format character outside the BMP is a surrogate pair, which no single char of it shows to be hidden.</summary>
    [Theory]
    [InlineData("\\udb40\\udc01", "\\U000e0001")]
    [InlineData("\\udb40\\udc41", "\\U000e0041")]
    [InlineData("\\ud834\\udd73", "\\U0001d173")]
    public void HiddenCharactersOutsideTheBmpAreRefusedAndShownEscaped(string character, string shown)
    {
        Write(Manifest(name: $"My{character}plugin", id: $"my{character}plugin"));

        var problems = Problems();

        Assert.Equal(
            [$"id 'my{shown}plugin' {PluginIds.Rule}", "id must not contain control or invisible formatting characters", "name must not contain control or invisible formatting characters"],
            problems);
        Assert.All(problems, p => Assert.DoesNotContain(p, IsHidden));
    }

    [Fact]
    public void ALetterOutsideTheBmpIsNotHidden()
    {
        // U+1F600 is a symbol, not a format character: it is shown as it is.
        Write(Manifest(name: "My \ud83d\ude00 plugin"));

        Assert.Equal("My 😀 plugin", PluginManifest.Load(_folder.Path).Name);
    }

    [Fact]
    public void APluginJsonThatCannotBeReadSaysWhy()
    {
        Write(Manifest());
        using var held = new FileStream(_folder.File("plugin.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var exception = Assert.Throws<PluginManifestException>(() => PluginManifest.Load(_folder.Path));

        var problem = Assert.Single(exception.Problems);
        Assert.StartsWith("plugin.json cannot be read: ", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("not valid JSON", problem, StringComparison.Ordinal);

        // An unreadable file is not an invalid one: the message says it once.
        Assert.True(exception.Unreadable);
        Assert.StartsWith("plugin.json cannot be read: ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not valid", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Every text the file holds, the languages, the licence and other systems' programs included.</summary>
    [Fact]
    public void TheLanguagesTheLicenceAndEveryProgramAreCheckedToo()
    {
        Write(Manifest(
            executable: $$"""{"{{Rid}}":"plugin.exe","other\u200b":"x\u202e.sh"}""",
            models: $$$"""[{"id":"m","title":"M","files":[{{{ModelFile()}}}],"languages":["en","uk\u2028"],"licence":{"name":"MIT\u202e","url":"https://example.com/\u200bLICENSE"}}]"""));

        var problems = Problems();

        Assert.Equal(
            [
                "system other\\u200b in executable must not contain control or invisible formatting characters",
                "program for other\\u200b must not contain control or invisible formatting characters",
                "model m: languages[1] must not contain control or invisible formatting characters",
                "model m: licence name must not contain control or invisible formatting characters",
                "model m: licence url must not contain control or invisible formatting characters",
            ],
            problems);
    }

    [Fact]
    public void AValueThatIsEchoedIsEscapedWhenItHoldsControlCharacters()
    {
        Write(Manifest(id: "bad\\u001b[2J", models: $$"""[{"id":"m\u0007","title":"M","files":[{{ModelFile(file: "a\\u001b.bin")}}],"languages":["en"]}]"""));

        var problems = Problems();

        Assert.Contains("id 'bad\\u001b[2J' ", problems[0], StringComparison.Ordinal);
        Assert.Contains(problems, p => p.Contains("model m\\u0007: file name must not contain control or invisible formatting characters", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain(p, IsHidden));
    }

    /// <summary>A null in a list is a problem with the file, not a crash of whatever reads it, such as 'dictum plugins list'.</summary>
    [Theory]
    [InlineData("[null]", "models[0] is empty")]
    [InlineData("""[{"id":"m","title":"M","files":[null],"languages":["en"]}]""", "model m: files[0] is empty")]
    [InlineData("""[{"id":"m","title":"M","files":[],"languages":["en"]}]""", "model m lists no files")]
    [InlineData("""[{"id":"m","title":"M","languages":["en"]}]""", "model m lists no files")]
    [InlineData("""[{"id":"m","title":"M","files":[FILE],"languages":["en",null]}]""", "model m: languages[1] is empty")]
    public void ANullEntryIsAProblem(string models, string problem)
    {
        Write(Manifest(models: models.Replace("FILE", ModelFile(), StringComparison.Ordinal)));

        Assert.Equal([problem], Problems());
    }

    [Fact]
    public void ANullProgramIsAProblem()
    {
        Write(Manifest(executable: $$"""{"{{Rid}}":null}"""));

        Assert.Equal([$"it has no program for {Rid}"], Problems());
    }

    [Fact]
    public void TheProgramForThisSystemMustBeInTheFolder()
    {
        Write(Manifest(executable: """{"other-rid":"plugin.exe"}"""));
        Assert.Equal([$"it has no program for {Rid}"], Problems());

        Write(Manifest(executable: $$"""{"{{Rid}}":"missing.exe"}"""));
        Assert.Equal([$"the program missing.exe for {Rid} is not in the folder"], Problems());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("sub/../../x")]
    public void AProgramPathOutsideTheFolderIsRefused(string file)
    {
        Write(Manifest(executable: $$"""{"{{Rid}}":"{{file}}"}"""));

        Assert.Equal([$"the program for {Rid} must be a file name inside the folder"], Problems());
    }

    [Fact]
    public void ARootedProgramPathIsRefused()
    {
        var rooted = Path.GetFullPath(_folder.File("plugin.exe")).Replace("\\", "\\\\", StringComparison.Ordinal);
        Write(Manifest(executable: $$"""{"{{Rid}}":"{{rooted}}"}"""));

        Assert.Equal([$"the program for {Rid} must be a file name inside the folder"], Problems());
    }

    /// <summary>The rules for model file names, on every system: a manifest is shared, so a path rooted on Windows is refused on Linux too.</summary>
    [Theory]
    [InlineData("\"\"", "program lists '', which must be a file or folder name inside the plugin's folder")]
    [InlineData("\".\"", "program lists '.', which must be a file or folder name inside the plugin's folder")]
    [InlineData("\"a/../b\"", "program lists 'a/../b', which must be a file or folder name inside the plugin's folder")]
    [InlineData("\"/abs\"", "program lists '/abs', which must be a file or folder name inside the plugin's folder")]
    [InlineData("\"C:\\\\x\"", "program lists 'C:\\x', which must be a file or folder name inside the plugin's folder")]
    [InlineData("null", "program[0] is empty")]
    [InlineData("\"plugin\\u202e.py\"", "program[0] must not contain control or invisible formatting characters")]
    public void ProgramEntriesFollowTheFileNameRules(string entry, string problem)
    {
        Write(Manifest(extra: $"\"program\": [{entry}],"));

        var problems = Problems();

        Assert.Equal([problem], problems);
        Assert.All(problems, p => Assert.DoesNotContain(p, IsHidden));
    }

    [Fact]
    public void AMissingProgramEntryIsAProblem()
    {
        Directory.CreateDirectory(_folder.File("lib"));
        Write(Manifest(extra: "\"program\": [\"plugin.py\", \"lib\", \"lib/missing\"],"));

        Assert.Equal(
            ["program lists 'plugin.py', which is not in the plugin's folder", "program lists 'lib/missing', which is not in the plugin's folder"],
            Problems());
    }

    /// <summary>
    /// A listed folder that is a link would add no files, and one on the way to an entry would lead out of the folder:
    /// either way the author would think a file is checked when it is not, so the manifest says so.
    /// </summary>
    [Fact]
    public void AProgramEntryThatIsALinkIsAProblem()
    {
        File.WriteAllText(_elsewhere.File("x.py"), "print()");
        _links.Add(_folder.File("lib"), _elsewhere.Path);
        Write(Manifest(extra: "\"program\": [\"lib\"],"));

        Assert.Equal(["program lists 'lib', which is a link or is inside a linked folder"], Problems());
    }

    [Fact]
    public void AProgramEntryInsideALinkedFolderIsAProblem()
    {
        Directory.CreateDirectory(Path.Combine(_elsewhere.Path, "sub"));
        File.WriteAllText(Path.Combine(_elsewhere.Path, "sub", "x.py"), "print()");
        Directory.CreateDirectory(_folder.File("src"));
        _links.Add(Path.Combine(_folder.Path, "src", "lib"), _elsewhere.Path);
        Write(Manifest(extra: "\"program\": [\"src/lib/sub/x.py\", \"src/lib/sub\"],"));

        Assert.Equal(
            ["program lists 'src/lib/sub/x.py', which is a link or is inside a linked folder", "program lists 'src/lib/sub', which is a link or is inside a linked folder"],
            Problems());
    }

    [Fact]
    public void AProgramEntryThatIsAFileLinkIsAProblem()
    {
        File.WriteAllText(_elsewhere.File("x.py"), "print()");
        try
        {
            File.CreateSymbolicLink(_folder.File("plugin.py"), _elsewhere.File("x.py"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This account cannot make symbolic links: {ex.Message}");
        }

        Write(Manifest(extra: "\"program\": [\"plugin.py\"],"));

        Assert.Equal(["program lists 'plugin.py', which is a link or is inside a linked folder"], Problems());
    }

    [Fact]
    public void AProgramOfFilesAndFoldersLoads()
    {
        File.WriteAllText(_folder.File("plugin.py"), "print()");
        Directory.CreateDirectory(_folder.File("lib"));
        Write(Manifest(extra: "\"program\": [\"plugin.py\", \"lib\"],"));

        Assert.Equal(["plugin.py", "lib"], PluginManifest.Load(_folder.Path).Program);
    }

    [Fact]
    public void AModelFileOverHttpIsRefused()
    {
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile(url: "http://example.com/a.bin")}}],"languages":["en"]}]"""));

        Assert.Equal(["model m: a.bin must come from an https address"], Problems());
    }

    [Fact]
    public void AModelFileWithoutAHashOrSizeIsRefused()
    {
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile(sha: "abc", size: 0)}}],"languages":["en"]}]"""));

        Assert.Equal(["model m: a.bin needs a SHA-256", "model m: a.bin needs its size in bytes"], Problems());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("My Model")]
    public void AModelIdThatIsNotASafeNameIsRefused(string id)
    {
        Write(Manifest(models: $$"""[{"id":"{{id}}","title":"M","files":[{{ModelFile()}}],"languages":["en"]}]"""));

        Assert.Equal([$"model id '{id}' {PluginIds.ModelRule}"], Problems());
    }

    [Theory]
    [InlineData("../model.onnx")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/")]
    public void AModelFileNameOutsideTheModelFolderIsRefused(string file)
    {
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile(file: file)}}],"languages":["en"]}]"""));

        Assert.Equal([$"model m: {file} must be a file name inside the model's folder"], Problems());
    }

    [Fact]
    public void ARootedModelFileNameIsRefused()
    {
        var rooted = OperatingSystem.IsWindows() ? "C:\\\\x" : "/etc/x";
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile(file: rooted)}}],"languages":["en"]}]"""));

        Assert.Single(Problems(), p => p.EndsWith("must be a file name inside the model's folder", StringComparison.Ordinal));
    }

    [Fact]
    public void AModelFileInASubfolderLoads()
    {
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile(file: "tokenizer/vocab.txt")}}],"languages":["en"]}]"""));

        Assert.Equal("tokenizer/vocab.txt", PluginManifest.Load(_folder.Path).Models![0].Files[0].File);
    }

    [Fact]
    public void ModelIdsMustBeUnique()
    {
        var model = $$"""{"id":"m","title":"M","files":[{{ModelFile()}}],"languages":["en"]}""";
        Write(Manifest(models: $"[{model},{model}]"));

        Assert.Equal(["model id 'm' appears twice"], Problems());
    }

    [Fact]
    public void AModelMustListItsLanguages()
    {
        Write(Manifest(models: $$"""[{"id":"m","title":"M","files":[{{ModelFile()}}],"languages":[]}]"""));

        Assert.Equal(["model m must list its languages"], Problems());
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        Write(Manifest(id: "Bad", name: "", models: $$"""[{"id":"m","title":"M","files":[{{ModelFile(url: "http://x/a")}}],"languages":["en"]}]"""));

        var problems = Problems();

        Assert.Equal(3, problems.Count);
        var exception = Assert.Throws<PluginManifestException>(() => PluginManifest.Load(_folder.Path));
        Assert.Equal($"plugin.json in {_folder.Path} is not valid: {string.Join("; ", problems)}", exception.Message);
    }

    /// <summary>The folder is the registration's path from config.json, so a message that names it spells out what it holds.</summary>
    [Fact]
    public void TheFolderInAMessageIsShownEscaped()
    {
        var folder = Path.Combine(_folder.Path, "a\u202eb\u001b[2J");

        var missing = Assert.Throws<PluginManifestException>(() => PluginManifest.Load(folder));
        var invalid = new PluginManifestException(folder, ["a problem"]);

        Assert.Contains("a\\u202eb\\u001b[2J", missing.Message, StringComparison.Ordinal);
        Assert.Contains("a\\u202eb\\u001b[2J", Assert.Single(missing.Problems), StringComparison.Ordinal);
        Assert.Equal($"plugin.json in {_folder.Path}{Path.DirectorySeparatorChar}a\\u202eb\\u001b[2J is not valid: a problem", invalid.Message);
        Assert.All(new[] { missing.Message, invalid.Message }, m => Assert.DoesNotContain(m, IsHidden));
    }

    private static bool IsHidden(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private IReadOnlyList<string> Problems() =>
        Assert.Throws<PluginManifestException>(() => PluginManifest.Load(_folder.Path)).Problems;

    private void Write(string json)
    {
        File.WriteAllText(_folder.File("plugin.exe"), "program");
        File.WriteAllText(_folder.File("plugin.json"), json);
    }

    private static string ModelFile(string url = "https://example.com/a.bin", string sha = ValidSha, int size = 42, string file = "a.bin") =>
        $$"""{"file":"{{file}}","url":"{{url}}","sha256":"{{sha}}","size":{{size}}}""";

    private static string Manifest(
        int protocol = 1,
        string id = "my-plugin",
        string name = "My plugin",
        string version = "1.2.0",
        string publisher = "Me",
        string? executable = null,
        string models = "[]",
        string extra = "") =>
        $$"""
        {
          {{extra}}
          "protocol": {{protocol}},
          "id": "{{id}}",
          "name": "{{name}}",
          "version": "{{version}}",
          "publisher": "{{publisher}}",
          "homepage": "https://example.com",
          "executable": {{executable ?? $$"""{"{{Rid}}":"plugin.exe"}"""}},
          "models": {{models}}
        }
        """;
}
