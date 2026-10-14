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
/// <param name="SpeechModel">Catalog id of the Whisper model.</param>
/// <param name="Vocabulary">Words to add to the first profile's list.</param>
/// <param name="ForceCpu">Use the CPU runtime even when a GPU is present.</param>
public sealed record SetupAnswers(
    string Language,
    string SpeechModel,
    CleanupChoice Cleanup,
    ExistingLlmServer? Server,
    IReadOnlyList<string> Vocabulary,
    bool ForceCpu = false);

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

    /// <summary>Used when <see cref="SttId"/> already names a provider that is not a local Whisper.</summary>
    private const string FallbackSttId = "whisper-local";

    public static string RecommendSpeechModel(HardwareInfo hardware) => hardware.GpuName is null ? CpuModel : GpuModel;

    /// <param name="isInstalled">Whether a catalog file is already in place (see <see cref="ModelFiles.IsInstalled"/>).</param>
    public static SetupPlan Plan(DictumConfig? current, SetupAnswers answers, Catalog catalog, Func<CatalogEntry, bool> isInstalled)
    {
        var speech = catalog.Get(answers.SpeechModel);
        if (speech.Kind != CatalogKind.Whisper)
        {
            throw new ConfigException($"'{speech.Id}' is not a speech model.");
        }

        if (!speech.Understands(answers.Language))
        {
            throw new ConfigException($"'{speech.Id}' only understands {string.Join(", ", speech.Languages)}; choose another model or language.");
        }

        var vad = catalog.Get(VadId);
        var config = current is null ? DefaultConfig.Create() : ConfigStore.Parse(ConfigStore.Serialize(current));

        var sttId = config.Stt.TryGetValue(SttId, out var named) && named is not WhisperLocalSttConfig ? FallbackSttId : SttId;
        var whisper = config.Stt.TryGetValue(sttId, out var existing) ? existing as WhisperLocalSttConfig : null;
        config.Stt[sttId] = (whisper ?? new WhisperLocalSttConfig { Model = speech.File }) with
        {
            Model = speech.File,
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
        if (answers is { Cleanup: CleanupChoice.ExistingServer, Server: { } server })
        {
            config.Processors[CleanupId] = config.Processors.TryGetValue(CleanupId, out var old) && old is LlmProcessorConfig llm
                ? llm with { BaseUrl = server.BaseUrl, Model = server.Model, ApiKey = server.ApiKey ?? llm.ApiKey }
                : new LlmProcessorConfig
                {
                    BaseUrl = server.BaseUrl,
                    Model = server.Model,
                    Prompt = "cleanup",
                    ApiKey = server.ApiKey,
                    TimeoutMs = ExistingServerTimeoutMs,
                };
            if (!pipeline.Contains(CleanupId))
            {
                pipeline.Add(CleanupId);
            }
        }
        else
        {
            pipeline.Remove(CleanupId);
        }

        config.Profiles[0] = first with
        {
            Stt = sttId,
            Language = answers.Language,
            Vocabulary = VocabularyList.Merge(first.Vocabulary, answers.Vocabulary),
            Pipeline = pipeline,
        };

        return new SetupPlan(config, [.. new[] { speech, vad }.Where(e => !isInstalled(e))]);
    }
}
