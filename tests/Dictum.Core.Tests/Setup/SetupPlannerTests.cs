using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.Core.Tests.Setup;

public class SetupPlannerTests
{
    private static readonly Catalog Catalog = Catalog.BuiltIn;

    [Fact]
    public void RecommendsTurboWithAGpuAndSmallWithout()
    {
        Assert.Equal("whisper-large-v3-turbo-q8", SetupPlanner.RecommendSpeechModel(new HardwareInfo("Intel(R) Iris(R) Xe Graphics", 1)));
        Assert.Equal("whisper-small-q8", SetupPlanner.RecommendSpeechModel(new HardwareInfo(null, 1)));
    }

    [Fact]
    public void FreshPlanDownloadsSpeechAndVadAndWritesAValidConfig()
    {
        var plan = SetupPlanner.Plan(null, Answers(vocabulary: ["Notepad++"]), Catalog, _ => false);

        Assert.Equal(["whisper-large-v3-turbo-q8", "vad-silero-6.2"], plan.Downloads.Select(d => d.Id));
        Assert.Equal(874188075 + 885098, plan.DownloadBytes);
        var stt = Assert.IsType<WhisperLocalSttConfig>(plan.Config.Stt["local"]);
        Assert.Equal("ggml-large-v3-turbo-q8_0.bin", stt.Model);
        Assert.True(stt.Vad);
        Assert.Equal("ggml-silero-v6.2.0.bin", stt.VadModel);
        Assert.Equal(WhisperRuntime.Auto, stt.Runtime);
        var profile = Assert.Single(plan.Config.Profiles);
        Assert.Equal(("uk", "local"), (profile.Language, profile.Stt));
        Assert.Equal(["Notepad++"], profile.Vocabulary);
        Assert.Empty(ConfigValidator.Validate(plan.Config));
    }

    [Fact]
    public void InstalledFilesAreNotDownloadedAgain() =>
        Assert.Empty(SetupPlanner.Plan(null, Answers(), Catalog, _ => true).Downloads);

    [Fact]
    public void ReRunKeepsEverythingElse()
    {
        var current = DefaultConfig.Create();
        current.Processors["terms"] = new ReplaceProcessorConfig();
        current.Profiles[0] = current.Profiles[0] with { Hotkey = "Ctrl+Alt+D", Pipeline = ["terms"], Vocabulary = ["Dictum"] };
        current.Profiles.Add(new ProfileConfig { Id = "english", Hotkey = "Ctrl+Alt+E", Stt = "local", Language = "en" });

        var plan = SetupPlanner.Plan(current, Answers(vocabulary: ["dictum", "VS Code"]), Catalog, _ => true);

        var first = plan.Config.Profiles[0];
        Assert.Equal("Ctrl+Alt+D", first.Hotkey);
        Assert.Equal(["terms"], first.Pipeline);
        Assert.Equal(["Dictum", "VS Code"], first.Vocabulary);
        Assert.Equal(Serialized(current.Profiles[1]), Serialized(plan.Config.Profiles[1]));
        Assert.True(plan.Config.Processors.ContainsKey("terms"));
    }

    [Fact]
    public void DoesNotChangeTheCurrentConfig()
    {
        var current = DefaultConfig.Create();
        var before = ConfigStore.Serialize(current);

        SetupPlanner.Plan(current, Answers(vocabulary: ["Dictum"]), Catalog, _ => false);

        Assert.Equal(before, ConfigStore.Serialize(current));
    }

    [Fact]
    public void ExistingServerAddsTheCleanupProcessorOnce()
    {
        var server = new ExistingLlmServer("http://192.168.1.10:8080/v1", "qwen3-4b", "secret:dictum-cleanup");
        var once = SetupPlanner.Plan(null, Answers(cleanup: CleanupChoice.ExistingServer, server: server), Catalog, _ => true);

        var twice = SetupPlanner.Plan(once.Config, Answers(cleanup: CleanupChoice.ExistingServer, server: server), Catalog, _ => true);

        Assert.Equal(["cleanup"], twice.Config.Profiles[0].Pipeline);
        var llm = Assert.IsType<LlmProcessorConfig>(twice.Config.Processors["cleanup"]);
        Assert.Equal(("http://192.168.1.10:8080/v1", "qwen3-4b", "cleanup", "secret:dictum-cleanup", 15000),
            (llm.BaseUrl, llm.Model, llm.Prompt, llm.ApiKey, llm.TimeoutMs));
        Assert.DoesNotContain(ConfigValidator.Validate(twice.Config), i => i.Severity == ConfigIssueSeverity.Error);
    }

    [Fact]
    public void NoCleanupRemovesItFromThePipelineButKeepsTheProcessor()
    {
        var server = new ExistingLlmServer("http://localhost:8080/v1", "m", null);
        var with = SetupPlanner.Plan(null, Answers(cleanup: CleanupChoice.ExistingServer, server: server), Catalog, _ => true);

        var without = SetupPlanner.Plan(with.Config, Answers(), Catalog, _ => true);

        Assert.Empty(without.Config.Profiles[0].Pipeline);
        Assert.True(without.Config.Processors.ContainsKey("cleanup"));
    }

    [Fact]
    public void ForceCpuSetsTheCpuRuntime() =>
        Assert.Equal(WhisperRuntime.Cpu,
            Assert.IsType<WhisperLocalSttConfig>(SetupPlanner.Plan(null, Answers() with { ForceCpu = true }, Catalog, _ => true).Config.Stt["local"]).Runtime);

    [Fact]
    public void EnglishOnlyModelIsRefusedForOtherLanguages()
    {
        var ex = Assert.Throws<ConfigException>(() =>
            SetupPlanner.Plan(null, Answers(language: "uk", speechModel: "whisper-tiny-en"), Catalog, _ => true));

        Assert.Contains("en", ex.Message);
        Assert.NotNull(SetupPlanner.Plan(null, Answers(language: "en", speechModel: "whisper-tiny-en"), Catalog, _ => true));
    }

    [Fact]
    public void ALanguageListIsWrittenInItsCanonicalForm() =>
        Assert.Equal("uk, en", SetupPlanner.Plan(null, Answers(language: "uk,en"), Catalog, _ => true).Config.Profiles[0].Language);

    [Fact]
    public void EnglishOnlyModelIsRefusedForALanguageList() =>
        Assert.Throws<ConfigException>(() =>
            SetupPlanner.Plan(null, Answers(language: "uk, en", speechModel: "whisper-tiny-en"), Catalog, _ => true));

    [Fact]
    public void AServerUrlWithoutASchemeIsRefusedWhenPlanning()
    {
        var ex = Assert.Throws<ConfigException>(() => SetupPlanner.Plan(
            null,
            Answers(cleanup: CleanupChoice.ExistingServer, server: new("localhost:8080/v1", "m", null)),
            Catalog,
            _ => true));

        Assert.Contains(ex.Issues, i => i.Path.EndsWith(".baseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void VadModelIsNotASpeechModel() =>
        Assert.Throws<ConfigException>(() => SetupPlanner.Plan(null, Answers(speechModel: "vad-silero-6.2"), Catalog, _ => true));

    [Fact]
    public void ARemoteProviderNamedLocalIsLeftAlone()
    {
        var current = DefaultConfig.Create();
        current.Stt["local"] = new OpenAiCompatibleSttConfig { BaseUrl = "http://box:8080/v1", Model = "whisper" };

        var plan = SetupPlanner.Plan(current, Answers(), Catalog, _ => true);

        Assert.IsType<OpenAiCompatibleSttConfig>(plan.Config.Stt["local"]);
        Assert.IsType<WhisperLocalSttConfig>(plan.Config.Stt["whisper-local"]);
        Assert.Equal("whisper-local", plan.Config.Profiles[0].Stt);
    }

    [Fact]
    public void ExistingServerTakesTheApiKeyAsGiven()
    {
        var withKey = SetupPlanner.Plan(null, Answers(cleanup: CleanupChoice.ExistingServer, server: new("https://api.openai.com/v1", "m", "env:OPENAI_API_KEY")), Catalog, _ => true);

        var withoutKey = SetupPlanner.Plan(withKey.Config, Answers(cleanup: CleanupChoice.ExistingServer, server: new("http://192.168.1.50:8080/v1", "qwen", null)), Catalog, _ => true);

        Assert.Null(Assert.IsType<LlmProcessorConfig>(withoutKey.Config.Processors["cleanup"]).ApiKey);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v2", true)]
    [InlineData("https://api.openai.com/v1", "http://192.168.1.50:8080/v1", false)]
    [InlineData("https://api.openai.com/v1", "http://api.openai.com/v1", false)]
    [InlineData("http://box:8080/v1", "http://box:9090/v1", false)]
    [InlineData("http://box:8080/v1", "not a url", false)]
    public void AKeyOnlyStaysWithTheSameServer(string before, string after, bool kept) =>
        Assert.Equal(kept ? "env:KEY" : null, SetupPlanner.CarriedApiKey(new ExistingLlmServer(before, "m", "env:KEY"), after));

    [Fact]
    public void NoCurrentServerCarriesNoKey() =>
        Assert.Null(SetupPlanner.CarriedApiKey(null, "http://box:8080/v1"));

    [Fact]
    public void AReplaceProcessorNamedCleanupStaysInThePipeline()
    {
        var plan = SetupPlanner.Plan(WithReplaceCleanup(), Answers(), Catalog, _ => true);

        Assert.Equal(["cleanup"], plan.Config.Profiles[0].Pipeline);
        Assert.IsType<ReplaceProcessorConfig>(plan.Config.Processors["cleanup"]);
    }

    [Fact]
    public void ExistingServerUsesAnotherIdWhenCleanupIsTaken()
    {
        var server = new ExistingLlmServer("http://localhost:8080/v1", "m", null);

        var plan = SetupPlanner.Plan(WithReplaceCleanup(), Answers(cleanup: CleanupChoice.ExistingServer, server: server), Catalog, _ => true);

        Assert.IsType<ReplaceProcessorConfig>(plan.Config.Processors["cleanup"]);
        Assert.IsType<LlmProcessorConfig>(plan.Config.Processors["llm-cleanup"]);
        Assert.Equal(["cleanup", "llm-cleanup"], plan.Config.Profiles[0].Pipeline);
        Assert.Equal(server, SetupPlanner.Defaults(plan.Config, new HardwareInfo(null, 1), Catalog, _ => true).Server);
    }

    [Fact]
    public void KeepingTheCurrentModelDownloadsOnlyVad()
    {
        var plan = SetupPlanner.Plan(DefaultConfig.Create(), Answers(speechModel: null), Catalog, _ => false);

        Assert.Equal("ggml-base.bin", Assert.IsType<WhisperLocalSttConfig>(plan.Config.Stt["local"]).Model);
        Assert.Equal(["vad-silero-6.2"], plan.Downloads.Select(d => d.Id));
    }

    [Fact]
    public void KeepingWithoutACurrentModelIsAConfigError() =>
        Assert.Throws<ConfigException>(() => SetupPlanner.Plan(null, Answers(speechModel: null), Catalog, _ => true));

    [Fact]
    public void TheProviderTheFirstProfileUsesIsUpdatedInPlace()
    {
        var current = DefaultConfig.Create();
        current.Stt.Clear();
        current.Stt["local-turbo"] = new WhisperLocalSttConfig { Model = "ggml-large-v3-turbo.bin", Threads = 8 };
        current.Profiles[0] = current.Profiles[0] with { Stt = "local-turbo" };

        var plan = SetupPlanner.Plan(current, Answers(), Catalog, _ => true);

        var stt = Assert.IsType<WhisperLocalSttConfig>(Assert.Single(plan.Config.Stt).Value);
        Assert.Equal(("ggml-large-v3-turbo-q8_0.bin", 8), (stt.Model, stt.Threads));
        Assert.Equal("local-turbo", plan.Config.Profiles[0].Stt);
    }

    [Fact]
    public void DefaultsOnAFreshMachineRecommendFromTheHardware()
    {
        var defaults = SetupPlanner.Defaults(null, new HardwareInfo("GPU", 1), Catalog, _ => false);

        Assert.Equal(new SetupDefaults("uk", "whisper-large-v3-turbo-q8", null, null), defaults);
    }

    [Fact]
    public void DefaultsKeepAHandPlacedModelAndAutoDetection()
    {
        var defaults = SetupPlanner.Defaults(DefaultConfig.Create(), new HardwareInfo("GPU", 1), Catalog, file => file == "ggml-base.bin");

        Assert.Equal(new SetupDefaults("auto", null, "ggml-base.bin", null), defaults);
    }

    [Fact]
    public void DefaultsUseTheCatalogIdOfTheCurrentModel()
    {
        var current = DefaultConfig.Create();
        current.Stt["local"] = new WhisperLocalSttConfig { Model = "ggml-small-q8_0.bin" };
        current.Profiles[0] = current.Profiles[0] with { Language = "uk" };

        var defaults = SetupPlanner.Defaults(current, new HardwareInfo("GPU", 1), Catalog, _ => false);

        Assert.Equal(("uk", "whisper-small-q8", (string?)null), (defaults.Language, defaults.SpeechModel, defaults.KeptModelFile));
    }

    [Fact]
    public void DefaultsRecommendAModelWhenTheCurrentFileIsGone() =>
        Assert.Equal("whisper-small-q8", SetupPlanner.Defaults(DefaultConfig.Create(), new HardwareInfo(null, 1), Catalog, _ => false).SpeechModel);

    private static DictumConfig WithReplaceCleanup()
    {
        var config = DefaultConfig.Create();
        config.Processors["cleanup"] = new ReplaceProcessorConfig { Rules = [new ReplaceRule { From = "ноутпад", To = "Notepad" }] };
        config.Profiles[0] = config.Profiles[0] with { Pipeline = ["cleanup"] };
        return config;
    }

    /// <summary>Profiles hold lists, which records compare by reference; compare what would be written instead.</summary>
    private static string Serialized(ProfileConfig profile) => ConfigStore.Serialize(new DictumConfig { Profiles = [profile] });

    [Theory]
    [InlineData(1L << 30, SetupPlanner.CpuModel)]
    [InlineData(8L << 30, SetupPlanner.GpuModel)]
    public void AGpuWithLittleMemoryGetsTheCpuModel(long memory, string expected) =>
        Assert.Equal(expected, SetupPlanner.RecommendSpeechModel(new HardwareInfo("GPU", long.MaxValue, memory)));

    [Fact]
    public void UnknownGpuMemoryKeepsTheGpuModel() =>
        Assert.Equal(SetupPlanner.GpuModel, SetupPlanner.RecommendSpeechModel(new HardwareInfo("GPU", long.MaxValue)));

    [Fact]
    public void GpuTextNamesSharedMemoryOrVram()
    {
        Assert.Equal("Intel(R) Iris(R) Xe Graphics, 15.8 GB shared memory",
            HardwareText.Gpu(new HardwareInfo("Intel(R) Iris(R) Xe Graphics", 0, 16_965_201_920, GpuIsIntegrated: true)));
        Assert.Equal("RTX 3060, 12.0 GB VRAM", HardwareText.Gpu(new HardwareInfo("RTX 3060", 0, 12L << 30)));
        Assert.Equal("RTX 3060", HardwareText.Gpu(new HardwareInfo("RTX 3060", 0)));
        Assert.Null(HardwareText.Gpu(new HardwareInfo(null, 0)));
    }

    private static SetupAnswers Answers(
        string language = "uk",
        string? speechModel = "whisper-large-v3-turbo-q8",
        CleanupChoice cleanup = CleanupChoice.None,
        ExistingLlmServer? server = null,
        string[]? vocabulary = null) =>
        new(language, speechModel, cleanup, server, vocabulary ?? []);

    // The wizard shows the current words and saves what is left in the box.
    [Fact]
    public void ReplacingWordsDropsTheOnesTheUserRemoved()
    {
        var current = DefaultConfig.Create();
        current.Profiles[0] = current.Profiles[0] with { Vocabulary = ["Nexus5", "Avalonia"] };

        var plan = SetupPlanner.Plan(current, Answers(vocabulary: ["Nexus5", "nexus5", " SoftServe "]) with { ReplaceVocabulary = true }, Catalog, _ => true);

        Assert.Equal(["Nexus5", "SoftServe"], plan.Config.Profiles[0].Vocabulary);
    }
}
