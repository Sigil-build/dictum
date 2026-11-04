using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictum.TestPlugin;

/// <summary>
/// What the test plugin does, step by step: the tests write it as <c>behaviour.json</c> next to the program or as the
/// <c>--model</c> file. Compiled into the test plugin and into Dictum.Testing, which writes it.
/// </summary>
public sealed record TestPluginBehaviour
{
    /// <summary>Where <see cref="LeaveChild"/> writes the pid of the process it starts, next to the program.</summary>
    public const string ChildPidFile = "child.pid";

    /// <summary>The transcription's text, unless <see cref="Echo"/> is set.</summary>
    public string Text { get; set; } = "canned text";

    /// <summary>The language the transcription answers with; none when null.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Answers with the form it got: <c>language=…;languages=…;prompt=…;model=…;response_format=…;file=…;type=…;bytes=…</c>,
    /// the last three of the file part: its name, its content type and its length.
    /// </summary>
    public bool Echo { get; set; }

    /// <summary><c>/health</c> answers 503 until this long after the start.</summary>
    public int LoadMs { get; set; }

    /// <summary>Exits with this code after the stderr lines, before it listens.</summary>
    public int? ExitAtStart { get; set; }

    /// <summary>
    /// Exits with code 1, as a crash would, this long after its first <c>/dictum/v1/info</c> answer was sent: the last
    /// step of Dictum's start, so the crash never lands inside the start, however slow the plugin's first requests are.
    /// </summary>
    public int? CrashAfterInfoMs { get; set; }

    /// <summary>
    /// Right before the crash that <see cref="CrashAfterInfoMs"/> plays, starts a copy of its own program that inherits its
    /// stdin, stdout and stderr, and writes the copy's pid to <see cref="ChildPidFile"/>. The copy runs for a minute
    /// unless it is ended, whatever its stdin does, unless <see cref="ChildFollowsStdin"/>.
    /// </summary>
    public bool LeaveChild { get; set; }

    /// <summary>The copy that <see cref="LeaveChild"/> starts exits when its stdin closes, as protocol 1 asks of a plugin.</summary>
    public bool ChildFollowsStdin { get; set; }

    /// <summary>The copy that <see cref="LeaveChild"/> starts prints <c>child here</c> on stdout this long after it starts.</summary>
    public int? ChildSaysAfterMs { get; set; }

    public bool NoReadyLine { get; set; }

    /// <summary>Announced in the ready line instead of the address it listens on.</summary>
    public string? ReadyUrl { get; set; }

    /// <summary><c>/health</c> answers this status with an error, instead of its own answer.</summary>
    public int? HealthStatus { get; set; }

    /// <summary>
    /// <c>/health</c> promises a longer body than it sends, then drops the connection: an answer that cannot be read.
    /// </summary>
    public bool HealthCutShort { get; set; }

    /// <summary><c>/dictum/v1/info</c> answers this status with an error.</summary>
    public int? InfoStatus { get; set; }

    /// <summary><c>/dictum/v1/info</c> answers 200 with this text instead of its JSON.</summary>
    public string? InfoBody { get; set; }

    /// <summary>Transcriptions answer this status with an error.</summary>
    public int? TranscribeStatus { get; set; }

    /// <summary>Kills its own process when a transcription arrives, before it answers, as a crash mid-request would.</summary>
    public bool ExitOnTranscribe { get; set; }

    /// <summary>Keeps running when its stdin closes, until it is killed.</summary>
    public bool IgnoreStdin { get; set; }

    /// <summary>
    /// A file next to the program, by its name, that gets a line appended first thing: a plugin that changes its own
    /// files when it runs, and a mark that it ran at all.
    /// </summary>
    public string? AppendAtStart { get; set; }

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
