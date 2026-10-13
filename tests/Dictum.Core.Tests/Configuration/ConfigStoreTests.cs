using System.Text.Json.Nodes;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;

namespace Dictum.Core.Tests.Configuration;

public class ConfigStoreTests
{
    // The example from the implementation plan, section 5.
    private const string PlanExample = """
        {
          "schemaVersion": 1,
          "general": { "startWithSystem": true, "historySize": 200, "sounds": true, "audioDevice": null },
          "stt": {
            "local-turbo": { "type": "whisper-local", "model": "ggml-large-v3-turbo.bin", "vad": true, "runtime": "auto", "threads": 8 },
            "llama-swap":  { "type": "openai-compatible", "baseUrl": "http://localhost:8080/v1", "model": "whisper-large-v3-turbo" },
            "groq":        { "type": "openai-compatible", "baseUrl": "https://api.groq.com/openai/v1", "model": "whisper-large-v3-turbo", "apiKey": "secret:groq" }
          },
          "processors": {
            "dev-terms":  { "type": "replace", "rules": [ { "from": "дот нет", "to": ".NET" }, { "pattern": "(?i)\\bавалоні[яї]\\b", "to": "Avalonia" } ] },
            "cleanup-uk": { "type": "llm", "baseUrl": "http://localhost:8080/v1", "model": "qwen3-4b", "prompt": "cleanup", "temperature": 0.2, "timeoutMs": 4000, "onError": "passthrough", "preserveText": true }
          },
          "profiles": [
            { "id": "dictate-uk", "name": "Диктовка UA", "hotkey": "Ctrl+Alt+Space", "mode": "PushToTalk",
              "stt": "local-turbo", "language": "uk", "sttPrompt": "Avalonia, .NET, llama.cpp",
              "vocabulary": ["Notepad++", "VS Code"],
              "pipeline": ["dev-terms", "cleanup-uk"], "output": "paste" },
            { "id": "raw-en", "name": "Raw EN", "hotkey": "Ctrl+Alt+E", "mode": "Toggle",
              "stt": "llama-swap", "language": "en", "pipeline": [], "output": "paste" }
          ]
        }
        """;

    [Fact]
    public void ParsesThePlanExample()
    {
        var config = ConfigStore.Parse(PlanExample);

        var turbo = Assert.IsType<WhisperLocalSttConfig>(config.Stt["local-turbo"]);
        Assert.Equal("ggml-large-v3-turbo.bin", turbo.Model);
        Assert.True(turbo.Vad);
        Assert.Equal(WhisperRuntime.Auto, turbo.Runtime);
        Assert.Equal(8, turbo.Threads);

        var groq = Assert.IsType<OpenAiCompatibleSttConfig>(config.Stt["groq"]);
        Assert.Equal("secret:groq", groq.ApiKey);

        var terms = Assert.IsType<ReplaceProcessorConfig>(config.Processors["dev-terms"]);
        Assert.Equal(".NET", terms.Rules[0].To);
        Assert.Equal(@"(?i)\bавалоні[яї]\b", terms.Rules[1].Pattern);

        var cleanup = Assert.IsType<LlmProcessorConfig>(config.Processors["cleanup-uk"]);
        Assert.Equal(LlmErrorMode.Passthrough, cleanup.OnError);

        Assert.Equal(HotkeyMode.Toggle, config.Profiles[1].Mode);
        Assert.Equal(["dev-terms", "cleanup-uk"], config.Profiles[0].Pipeline);
        Assert.True(cleanup.PreserveText);
        Assert.Equal(["Notepad++", "VS Code"], config.Profiles[0].Vocabulary);
        Assert.Empty(config.Profiles[1].Vocabulary);
        Assert.Empty(ConfigValidator.Validate(config).Select(i => i.ToString()));
    }

    [Fact]
    public void SerializeRoundTripsAndKeepsTheTypeDiscriminator()
    {
        var original = ConfigStore.Parse(PlanExample);

        var json = ConfigStore.Serialize(original);
        var copy = ConfigStore.Parse(json);

        Assert.Contains("\"type\": \"whisper-local\"", json);
        Assert.Contains("\"output\": \"paste\"", json);
        Assert.Contains("\"runtime\": \"auto\"", json);
        Assert.Contains("\"vocabulary\": [", json);
        Assert.Contains("\"preserveText\": true", json);
        Assert.Equal(ConfigStore.Serialize(copy), json);
    }

    [Fact]
    public void TypeDiscriminatorMayComeAfterOtherProperties()
    {
        var config = ConfigStore.Parse("""{ "stt": { "a": { "model": "m.bin", "type": "whisper-local" } } }""");

        Assert.IsType<WhisperLocalSttConfig>(config.Stt["a"]);
    }

    [Fact]
    public void SyntaxErrorReportsTheLine()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Parse("{\n  \"general\": {\n    \"sounds\": tru\n  }\n}"));

        Assert.Equal(3, Assert.Single(ex.Issues).Line);
    }

    [Fact]
    public void UnknownProviderTypeIsAnError()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            ConfigStore.Parse("{\n  \"stt\": {\n    \"a\": { \"type\": \"nope\", \"model\": \"m\" }\n  }\n}"));

        Assert.NotNull(Assert.Single(ex.Issues).Line);
    }

    [Fact]
    public void MissingRequiredPropertyIsAnError()
    {
        var ex = Assert.Throws<ConfigException>(() => ConfigStore.Parse("""{ "stt": { "a": { "type": "whisper-local" } } }"""));

        Assert.Contains("model", Assert.Single(ex.Issues).Message);
    }

    [Fact]
    public void LoadThrowsWithHintWhenFileIsMissing()
    {
        using var dir = new TempDirectory();
        var store = new ConfigStore(dir.File("config.json"), NullLog.Instance);

        var ex = Assert.Throws<ConfigException>(store.Load);

        Assert.Contains("dictum config init", ex.Message);
    }

    [Fact]
    public void LoadRejectsSemanticErrors()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("config.json"), """{ "profiles": [ { "id": "p", "stt": "missing" } ] }""");
        var store = new ConfigStore(dir.File("config.json"), NullLog.Instance);

        var ex = Assert.Throws<ConfigException>(store.Load);

        Assert.Equal("profiles[0].stt", Assert.Single(ex.Issues).Path);
    }

    [Fact]
    public void SaveThenLoadRoundTripsTheDefaultConfig()
    {
        using var dir = new TempDirectory();
        var store = new ConfigStore(dir.File(Path.Combine("sub", "config.json")), NullLog.Instance);

        store.Save(DefaultConfig.Create());
        var loaded = store.Load();

        Assert.Equal(DefaultConfig.DefaultModel, Assert.IsType<WhisperLocalSttConfig>(loaded.Stt[DefaultConfig.LocalSttId]).Model);
        Assert.Equal("dictate", Assert.Single(loaded.Profiles).Id);
    }

    [Fact]
    public void OlderSchemaIsMigratedAndBackedUp()
    {
        using var dir = new TempDirectory();
        var path = dir.File("config.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "general": { "historySize": 5 } }""");
        var store = new ConfigStore(path, [new BumpHistorySize()], currentVersion: 2, NullLog.Instance);

        var config = store.Load();

        Assert.Equal(2, config.SchemaVersion);
        Assert.Equal(50, config.General.HistorySize);
        Assert.Single(Directory.GetFiles(dir.Path, "config.json.v1.*.bak"));
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(path));
    }

    [Fact]
    public void NewerSchemaIsRejected()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("config.json"), """{ "schemaVersion": 99 }""");
        var store = new ConfigStore(dir.File("config.json"), NullLog.Instance);

        var ex = Assert.Throws<ConfigException>(store.Load);

        Assert.Contains("newer", ex.Message);
    }

    private sealed class BumpHistorySize : IConfigMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject root) => root["general"]!["historySize"] = 50;
    }
}
