using Dictum.Core;
using Dictum.Core.Configuration;
using Dictum.Core.Vocabulary;

namespace Dictum.Setup;

/// <param name="GpuName">The hardware GPU Whisper can use, or null when only the CPU (or a software renderer) is there.</param>
public sealed record HardwareInfo(string? GpuName, long FreeDiskBytes);

public enum CleanupChoice
{
    None,
    ExistingServer,
}

/// <param name="ApiKey">The processor's apiKey value: <c>secret:name</c>, <c>env:VAR</c>, or null for none.</param>
public sealed record ExistingLlmServer(string BaseUrl, string Model, string? ApiKey);

/// <param name="Language">ISO 639-1 code or "auto".</param>
/// <param name="SpeechModel">Catalog id of the Whisper model, or null to keep the current one.</param>
/// <param name="Vocabulary">Words to add to the first profile's list.</param>
/// <param name="ForceCpu">Use the CPU runtime even when a GPU is present.</param>
public sealed record SetupAnswers(
    string Language,
    string? SpeechModel,
    CleanupChoice Cleanup,
    ExistingLlmServer? Server,
    IReadOnlyList<string> Vocabulary,
    bool ForceCpu = false);

/// <summary>What <c>dictum init</c> offers first: the current config's choices, or recommendations on a fresh machine.</summary>
/// <param name="SpeechModel">Catalog id to offer first, or null to keep <paramref name="KeptModelFile"/>.</param>
/// <param name="KeptModelFile">A model file outside the catalog that the config already uses and that exists.</param>
public sealed record SetupDefaults(string Language, string? SpeechModel, string? KeptModelFile, ExistingLlmServer? Server);

/// <param name="Downloads">Catalog files that are not installed yet.</param>
public sealed record SetupPlan(DictumConfig Config, IReadOnlyList<CatalogEntry> Downloads)
{
    public long DownloadBytes => Downloads.Sum(d => d.Size);
}

public static class ModelFiles
{
    /// <summary>The file is in the models directory with the catalog's size; its hash was checked when it was downloaded.</summary>
    public static bool IsInstalled(AppPaths paths, CatalogEntry entry)
    {
        var path = paths.ResolveModel(entry.File);
        return File.Exists(path) && new FileInfo(path).Length == entry.Size;
    }
}

/// <summary>Turns the answers to <c>dictum init</c> and the current config into the config to write and the files to fetch.</summary>
public static class SetupPlanner
{
    public const string SttId = "local";
    public const string CleanupId = "cleanup";
    public const string VadId = "vad-silero-6.2";
    public const string GpuModel = "whisper-large-v3-turbo-q8";
    public const string CpuModel = "whisper-small-q8";
    public const int ExistingServerTimeoutMs = 15_000;

    /// <summary>Used when <see cref="CleanupId"/> already names a processor of the user's that is not an LLM.</summary>
    public const string LlmCleanupFallbackId = "llm-cleanup";

    /// <summary>Used when <see cref="SttId"/> already names a provider that is not a local Whisper.</summary>
    private const string FallbackSttId = "whisper-local";

    public static string RecommendSpeechModel(HardwareInfo hardware) => hardware.GpuName is null ? CpuModel : GpuModel;

    /// <summary>
    /// The provider init manages: the local Whisper provider the first profile uses, otherwise <see cref="SttId"/>
    /// (or <see cref="FallbackSttId"/> when that id already names something else).
    /// </summary>
    public static string SpeechProviderId(DictumConfig config) =>
        config.Profiles.FirstOrDefault() is { } first && config.Stt.TryGetValue(first.Stt, out var used) && used is WhisperLocalSttConfig
            ? first.Stt
            : config.Stt.TryGetValue(SttId, out var named) && named is not WhisperLocalSttConfig ? FallbackSttId : SttId;

    /// <param name="modelFileExists">Whether a model file name (as written in the config) exists on disk.</param>
    public static SetupDefaults Defaults(DictumConfig? current, HardwareInfo hardware, Catalog catalog, Func<string, bool> modelFileExists)
    {
        var first = current?.Profiles.FirstOrDefault();
        var language = first is null ? "uk" : first.Language ?? "auto";
        var recommended = RecommendSpeechModel(hardware);
        if (current is null)
        {
            return new SetupDefaults(language, recommended, null, null);
        }

        var whisper = current.Stt.TryGetValue(SpeechProviderId(current), out var stt) ? stt as WhisperLocalSttConfig : null;
        if (whisper is not null && catalog.ForFile(whisper.Model) is { Kind: CatalogKind.Whisper } entry)
        {
            return new SetupDefaults(language, entry.Id, null, CurrentServer(current));
        }

        return whisper is not null && modelFileExists(whisper.Model)
            ? new SetupDefaults(language, null, whisper.Model, CurrentServer(current))
            : new SetupDefaults(language, recommended, null, CurrentServer(current));
    }

    /// <summary>The LLM cleanup processor init manages; a user's own non-LLM processor named "cleanup" is left alone.</summary>
    public static string CleanupProcessorId(DictumConfig config) =>
        config.Processors.TryGetValue(CleanupId, out var named) && named is not LlmProcessorConfig ? LlmCleanupFallbackId : CleanupId;

    private static ExistingLlmServer? CurrentServer(DictumConfig config) =>
        config.Profiles.FirstOrDefault() is { } first
        && first.Pipeline.Contains(CleanupProcessorId(config))
        && config.Processors.TryGetValue(CleanupProcessorId(config), out var processor)
        && processor is LlmProcessorConfig llm
            ? new ExistingLlmServer(llm.BaseUrl, llm.Model, llm.ApiKey)
            : null;

    /// <param name="isInstalled">Whether a catalog file is already in place (see <see cref="ModelFiles.IsInstalled"/>).</param>
    public static SetupPlan Plan(DictumConfig? current, SetupAnswers answers, Catalog catalog, Func<CatalogEntry, bool> isInstalled)
    {
        var vad = catalog.Get(VadId);
        var config = current is null ? DefaultConfig.Create() : ConfigStore.Parse(ConfigStore.Serialize(current));
        var sttId = SpeechProviderId(config);
        var whisper = config.Stt.TryGetValue(sttId, out var existing) ? existing as WhisperLocalSttConfig : null;

        CatalogEntry? speech = null;
        if (answers.SpeechModel is { } id)
        {
            speech = catalog.Get(id);
            if (speech.Kind != CatalogKind.Whisper)
            {
                throw new ConfigException($"'{speech.Id}' is not a speech model.");
            }

            if (!speech.Understands(answers.Language))
            {
                throw new ConfigException($"'{speech.Id}' only understands {string.Join(", ", speech.Languages)}; choose another model or language.");
            }
        }
        else if (current is null || whisper is null)
        {
            throw new ConfigException("There is no current speech model to keep; choose one with --stt.");
        }

        var model = speech?.File ?? whisper!.Model;
        config.Stt[sttId] = (whisper ?? new WhisperLocalSttConfig { Model = model }) with
        {
            Model = model,
            Vad = true,
            VadModel = vad.File,
            Runtime = answers.ForceCpu ? WhisperRuntime.Cpu : whisper?.Runtime ?? WhisperRuntime.Auto,
        };

        if (config.Profiles.Count == 0)
        {
            config.Profiles.Add(DefaultConfig.Create().Profiles[0]);
        }

        var first = config.Profiles[0];
        var pipeline = first.Pipeline.ToList();
        var cleanupId = CleanupProcessorId(config);
        if (answers is { Cleanup: CleanupChoice.ExistingServer, Server: { } server })
        {
            config.Processors[cleanupId] = config.Processors.TryGetValue(cleanupId, out var old) && old is LlmProcessorConfig llm
                ? llm with { BaseUrl = server.BaseUrl, Model = server.Model, ApiKey = server.ApiKey ?? llm.ApiKey }
                : new LlmProcessorConfig
                {
                    BaseUrl = server.BaseUrl,
                    Model = server.Model,
                    Prompt = "cleanup",
                    ApiKey = server.ApiKey,
                    TimeoutMs = ExistingServerTimeoutMs,
                };
            if (!pipeline.Contains(cleanupId))
            {
                pipeline.Add(cleanupId);
            }
        }
        else
        {
            pipeline.Remove(cleanupId);
        }

        config.Profiles[0] = first with
        {
            Stt = sttId,
            Language = answers.Language,
            Vocabulary = VocabularyList.Merge(first.Vocabulary, answers.Vocabulary),
            Pipeline = pipeline,
        };

        CatalogEntry[] wanted = speech is null ? [vad] : [speech, vad];
        return new SetupPlan(config, [.. wanted.Where(e => !isInstalled(e))]);
    }
}
