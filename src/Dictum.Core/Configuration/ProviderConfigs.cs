using System.Text.Json.Serialization;

namespace Dictum.Core.Configuration;

/// <summary>
/// Base for STT provider settings. The <c>type</c> property selects the derived record.
/// A new provider is a derived record here, an implementation and one registry line in Dictum.Host.
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
    public const string DefaultVadModel = "ggml-silero-v6.2.0.bin";

    /// <summary>File name in the models directory, or an absolute path.</summary>
    public required string Model { get; init; }

    public bool Vad { get; init; }

    /// <summary>Silero VAD model; file name in the models directory, or an absolute path.</summary>
    public string VadModel { get; set; } = DefaultVadModel;

    public WhisperRuntime Runtime { get; set; } = WhisperRuntime.Auto;

    /// <summary>0 picks a default from the CPU count.</summary>
    public int Threads { get; init; }
}

public sealed record OpenAiCompatibleSttConfig : SttConfig
{
    public const string TypeName = "openai-compatible";
    public const int DefaultTimeoutMs = 60_000;

    /// <summary>Base URL including the version segment, e.g. http://localhost:8080/v1.</summary>
    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    /// <summary>Literal key, <c>env:VAR</c> or <c>secret:name</c>.</summary>
    public string? ApiKey { get; init; }

    public int TimeoutMs { get; set; } = DefaultTimeoutMs;
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

    /// <summary>A literal rule matches whole words only: "мейн" leaves "мейнфрейм" alone. Regex rules write their own boundaries.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WholeWord { get; set; }

    /// <summary>The rule stays in the list but is not applied (the Settings table's "On" column, off).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
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
    public const int DefaultTimeoutMs = 4_000;
    public const double DefaultTemperature = 0.2;

    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    /// <summary>Template name: <c>prompts/&lt;name&gt;.md</c> in the config directory, or a built-in one.</summary>
    public required string Prompt { get; init; }

    public string? ApiKey { get; init; }

    public double Temperature { get; set; } = DefaultTemperature;

    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    public LlmErrorMode OnError { get; set; } = LlmErrorMode.Passthrough;

    /// <summary>
    /// The answer must keep the dictated text and only fix it: answers with new line breaks, far fewer words or
    /// another script are rejected. Null means on for the built-in <c>cleanup</c> prompt only.
    /// </summary>
    public bool? PreserveText { get; init; }

    /// <summary>
    /// Upper limit for the answer in tokens. Null scales it to the input; raise it for models that reason before
    /// answering, or turn reasoning off on the server.
    /// </summary>
    public int? MaxTokens { get; init; }
}
