using System.Text.Json.Serialization;

namespace Dictum.Core.Configuration;

/// <summary>
/// Base for STT provider settings. The <c>type</c> property selects the derived record.
/// A new provider = a derived record here + an implementation + one registry line in Dictum.Host.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WhisperLocalSttConfig), WhisperLocalSttConfig.TypeName)]
[JsonDerivedType(typeof(OpenAiCompatibleSttConfig), OpenAiCompatibleSttConfig.TypeName)]
public abstract record SttConfig;

public enum WhisperRuntime
{
    /// <summary>GPU runtimes first, then CPU.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    [JsonStringEnumMemberName("cpu")]
    Cpu,

    [JsonStringEnumMemberName("vulkan")]
    Vulkan,

    [JsonStringEnumMemberName("cuda")]
    Cuda,
}

public sealed record WhisperLocalSttConfig : SttConfig
{
    public const string TypeName = "whisper-local";

    /// <summary>File name in the models directory, or an absolute path.</summary>
    public required string Model { get; init; }

    public bool Vad { get; init; }

    /// <summary>Silero VAD model; file name in the models directory, or an absolute path.</summary>
    public string VadModel { get; set; } = "ggml-silero-v6.2.0.bin";

    public WhisperRuntime Runtime { get; set; } = WhisperRuntime.Auto;

    /// <summary>0 picks a default from the CPU count.</summary>
    public int Threads { get; init; }
}

public sealed record OpenAiCompatibleSttConfig : SttConfig
{
    public const string TypeName = "openai-compatible";

    /// <summary>Base URL including the version segment, e.g. http://localhost:8080/v1.</summary>
    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    /// <summary>Literal key, <c>env:VAR</c> or <c>secret:name</c>.</summary>
    public string? ApiKey { get; init; }

    public int TimeoutMs { get; set; } = 60_000;
}

/// <summary>Base for text processor settings. The <c>type</c> property selects the derived record.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReplaceProcessorConfig), ReplaceProcessorConfig.TypeName)]
[JsonDerivedType(typeof(LlmProcessorConfig), LlmProcessorConfig.TypeName)]
public abstract record ProcessorConfig;

public sealed record ReplaceProcessorConfig : ProcessorConfig
{
    public const string TypeName = "replace";

    public List<ReplaceRule> Rules { get; set; } = [];
}

/// <summary>Either a literal <see cref="From"/> or a regex <see cref="Pattern"/>, replaced by <see cref="To"/>.</summary>
public sealed record ReplaceRule
{
    public string? From { get; init; }

    public string? Pattern { get; init; }

    public string To { get; set; } = string.Empty;

    /// <summary>Applies to literal rules; regex rules use inline flags such as <c>(?i)</c>.</summary>
    public bool IgnoreCase { get; set; } = true;
}

public enum LlmErrorMode
{
    /// <summary>Return the input text unchanged and log a warning.</summary>
    [JsonStringEnumMemberName("passthrough")]
    Passthrough,

    /// <summary>Fail the dictation.</summary>
    [JsonStringEnumMemberName("fail")]
    Fail,
}

public sealed record LlmProcessorConfig : ProcessorConfig
{
    public const string TypeName = "llm";

    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    /// <summary>Template name: <c>prompts/&lt;name&gt;.md</c> in the config directory, or a built-in one.</summary>
    public required string Prompt { get; init; }

    public string? ApiKey { get; init; }

    public double Temperature { get; set; } = 0.2;

    public int TimeoutMs { get; set; } = 4_000;

    public LlmErrorMode OnError { get; set; } = LlmErrorMode.Passthrough;

    /// <summary>
    /// The answer must keep the dictated text and only fix it: answers with new line breaks, far fewer words or
    /// another script are rejected. Null means on for the built-in <c>cleanup</c> prompt only.
    /// </summary>
    public bool? PreserveText { get; init; }
}
