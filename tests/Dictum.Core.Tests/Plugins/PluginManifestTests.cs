using Dictum.Core.Configuration;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Core.Tests.Plugins;

public sealed class PluginManifestTests : IDisposable
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDirectory _folder = new();

    private static string Rid => PluginProtocol.CurrentRid;

    public void Dispose() => _folder.Dispose();

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

    private IReadOnlyList<string> Problems() =>
        Assert.Throws<PluginManifestException>(() => PluginManifest.Load(_folder.Path)).Problems;

    private void Write(string json)
    {
        File.WriteAllText(_folder.File("plugin.exe"), "program");
        File.WriteAllText(_folder.File("plugin.json"), json);
    }

    private static string ModelFile(string url = "https://example.com/a.bin", string sha = ValidSha, int size = 42) =>
        $$"""{"file":"a.bin","url":"{{url}}","sha256":"{{sha}}","size":{{size}}}""";

    private static string Manifest(
        int protocol = 1,
        string id = "my-plugin",
        string name = "My plugin",
        string version = "1.2.0",
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
          "publisher": "Me",
          "homepage": "https://example.com",
          "executable": {{executable ?? $$"""{"{{Rid}}":"plugin.exe"}"""}},
          "models": {{models}}
        }
        """;
}
