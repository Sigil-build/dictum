using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.TestPlugin;

/// <summary>
/// What the test plugin does, step by step: the tests write it as <c>behaviour.json</c> next to the program or as the
/// <c>--model</c> file. Compiled into the test plugin and into Dictum.Testing, which writes it.
/// </summary>
public sealed record TestPluginBehaviour
{
    /// <summary>The transcription's text, unless <see cref="Echo"/> is set.</summary>
    public string Text { get; set; } = "canned text";

    /// <summary>The language the transcription answers with; none when null.</summary>
    public string? Language { get; set; }

    /// <summary>Answers with the form it got: <c>language=…;languages=…;prompt=…;model=…;bytes=…</c>.</summary>
    public bool Echo { get; set; }

    /// <summary><c>/health</c> answers 503 until this long after the start.</summary>
    public int LoadMs { get; set; }

    /// <summary>Exits with this code after the stderr lines, before it listens.</summary>
    public int? ExitAtStart { get; set; }

    /// <summary>
    /// Exits with code 1, as a crash would, this long after its first <c>/dictum/v1/info</c> answer was sent: the last
    /// step of Dictum's start, so the crash never lands inside the start, however slow the plugin's first requests are.
    /// </summary>
    public int? ExitAfterReadyMs { get; set; }

    public bool NoReadyLine { get; set; }

    /// <summary>Announced in the ready line instead of the address it listens on.</summary>
    public string? ReadyUrl { get; set; }

    /// <summary>Transcriptions answer this status with an error.</summary>
    public int? TranscribeStatus { get; set; }

    /// <summary>Kills its own process when a transcription arrives, before it answers, as a crash mid-request would.</summary>
    public bool ExitOnTranscribe { get; set; }

    /// <summary>Keeps running when its stdin closes, until it is killed.</summary>
    public bool IgnoreStdin { get; set; }

    /// <summary>Written to stderr first thing.</summary>
    public IReadOnlyList<string> Stderr { get; set; } = [];

    /// <summary>A stdout line before the ready line.</summary>
    public string? Banner { get; set; }

    /// <summary>A stdout line after the ready line and the flood.</summary>
    public string? AfterReady { get; set; }

    /// <summary>Lines written to stdout and to stderr each, right after the ready line.</summary>
    public int FloodLines { get; set; }

    public int InfoProtocol { get; set; } = 1;

    public string Name { get; set; } = "Test";

    public string Version { get; set; } = "1.0.0";

    public IReadOnlyList<string> Languages { get; set; } = ["en"];

    public bool SetLanguage { get; set; } = true;

    public static TestPluginBehaviour Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), TestPluginBehaviourJson.Default.TestPluginBehaviour) ?? new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, TestPluginBehaviourJson.Default.TestPluginBehaviour));
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TestPluginBehaviour))]
internal sealed partial class TestPluginBehaviourJson : JsonSerializerContext;
