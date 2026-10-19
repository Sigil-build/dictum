using System.Text.Json;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Pipeline;
using Dictum.Host.Ipc;
using Dictum.Platform;

namespace Dictum.Host.Tests.Ipc;

/// <summary>
/// The IPC messages as they go over the socket, spelled out. The daemon, the desktop app, the CLI and scripts of
/// different versions meet on this wire during an upgrade, and they all read the same constants, so only literal text
/// catches a changed value.
/// </summary>
public sealed class IpcWireTests
{
    private static readonly float[] Bands = [0.5f, 0.125f];

    [Theory]
    [InlineData(IpcCommand.Toggle, "toggle")]
    [InlineData(IpcCommand.Start, "start")]
    [InlineData(IpcCommand.Stop, "stop")]
    [InlineData(IpcCommand.Cancel, "cancel")]
    [InlineData(IpcCommand.Status, "status")]
    [InlineData(IpcCommand.Reload, "reload")]
    [InlineData(IpcCommand.Subscribe, "subscribe")]
    [InlineData(IpcCommand.Shutdown, "shutdown")]
    [InlineData(IpcCommand.PauseHotkeys, "pause-hotkeys")]
    [InlineData(IpcCommand.ResumeHotkeys, "resume-hotkeys")]
    [InlineData(IpcCommand.UseProfile, "use-profile")]
    [InlineData(IpcCommand.Devices, "devices")]
    [InlineData(IpcCommand.Doctor, "doctor")]
    [InlineData(IpcCommand.TestStt, "test-stt")]
    [InlineData(IpcCommand.MonitorStart, "monitor-start")]
    [InlineData(IpcCommand.MonitorStop, "monitor-stop")]
    public void EachCommandGoesOutUnderItsName(string command, string wire) =>
        Assert.Equal($$"""{"command":"{{wire}}"}""", Serialize(new IpcRequest(command)));

    [Fact]
    public void ARequestWritesEveryFieldItHasInCamelCase() =>
        Assert.Equal(
            """{"command":"test-stt","profile":"dictate","stt":"local","device":"USB Mic","seconds":3,"sample":true}""",
            Serialize(new IpcRequest(IpcCommand.TestStt, "dictate", "local", "USB Mic", 3, true)));

    [Fact]
    public void ARequestFromAnotherVersionIsReadByItsNames() =>
        Assert.Equal(
            new IpcRequest(IpcCommand.UseProfile, "dictate-uk"),
            JsonSerializer.Deserialize("""{"command":"use-profile","profile":"dictate-uk"}""", IpcJsonContext.Default.IpcRequest));

    [Fact]
    public void AFailureIsOkFalseAndItsError() =>
        Assert.Equal("""{"ok":false,"error":"Malformed request."}""", Serialize(IpcResponse.Failure("Malformed request.")));

    [Fact]
    public void AStatusWritesEveryField()
    {
        var status = new DaemonStatus(
            PipelineState.Recording.ToString(),
            "dictate",
            [new ProfileStatus("dictate", "Ctrl+Alt+Space", HotkeyMode.PushToTalk.ToString(), true)],
            "low-level keyboard hook (SharpHook)",
            "SendInput",
            4242,
            "The keyboard hook did not start in time.",
            Paused: true,
            SelectedProfile: "dictate",
            ConfigWarnings: ["profiles[0]: no hotkey"],
            WarmedUp: true);

        Assert.Equal(
            """{"ok":true,"status":{"state":"Recording","activeProfile":"dictate","profiles":[{"id":"dictate","hotkey":"Ctrl\u002BAlt\u002BSpace","mode":"PushToTalk","registered":true}],"hotkeyBackend":"low-level keyboard hook (SharpHook)","injectorBackend":"SendInput","processId":4242,"warning":"The keyboard hook did not start in time.","paused":true,"selectedProfile":"dictate","configWarnings":["profiles[0]: no hotkey"],"protocol":1,"warmedUp":true}}""",
            Serialize(IpcResponse.Success(status)));
    }

    [Fact]
    public void AnAnswerCarriesDevicesChecksAndATestResult()
    {
        var response = new IpcResponse(
            true,
            Devices: [new AudioDevice("3", "USB Mic", "WASAPI", 1, 48000, true)],
            Checks: [new PlatformCheck("hotkeys", HealthStatus.Warning("Not registered", "Pick another hotkey"))],
            Test: new SttTestResult("Привіт", "uk", 850, "Vulkan", "ggml-large-v3-turbo-q8_0.bin", 2.5));

        Assert.Equal(
            """{"ok":true,"devices":[{"id":"3","name":"USB Mic","hostApi":"WASAPI","maxInputChannels":1,"defaultSampleRate":48000,"isDefault":true}],"checks":[{"name":"hotkeys","status":{"level":"Warning","detail":"Not registered","hint":"Pick another hotkey"}}],"test":{"text":"\u041F\u0440\u0438\u0432\u0456\u0442","language":"uk","latencyMs":850,"backend":"Vulkan","model":"ggml-large-v3-turbo-q8_0.bin","audioSeconds":2.5}}""",
            Serialize(response));
    }

    [Fact]
    public void EachEventKindHasItsShape()
    {
        var idle = new DaemonStatus(PipelineState.Idle.ToString(), null, [], "none (use 'dictum toggle')", "clipboard", 7);

        string[] events =
        [
            Serialize(IpcEvent.ForState(PipelineState.Transcribing, "dictate", HotkeyMode.Toggle.ToString())),
            Serialize(IpcEvent.ForFrame(new AudioFrame(0.25f, Bands, TimeSpan.FromMilliseconds(1520)))),
            Serialize(IpcEvent.Finished(DictationOutcome.Pasted, null, "uk", 2300, 900)),
            Serialize(IpcEvent.Finished(DictationOutcome.Error, "The server did not answer.", null, 1200, null)),
            Serialize(IpcEvent.ForStatus(idle)),
            Serialize(IpcEvent.Stopping(StoppingReason.Shutdown)),
            Serialize(IpcEvent.Stopping(StoppingReason.Exit)),
        ];

        Assert.Equal(
            [
                """{"event":"state","state":"Transcribing","profile":"dictate","mode":"Toggle"}""",
                """{"event":"frame","t":1.52,"rms":0.25,"bands":[0.5,0.125]}""",
                """{"event":"finished","outcome":"pasted","language":"uk","durationMs":2300,"transcribeMs":900}""",
                """{"event":"finished","outcome":"error","message":"The server did not answer.","durationMs":1200}""",
                """{"event":"status","status":{"state":"Idle","profiles":[],"hotkeyBackend":"none (use \u0027dictum toggle\u0027)","injectorBackend":"clipboard","processId":7,"paused":false,"protocol":1}}""",
                """{"event":"stopping","reason":"shutdown"}""",
                """{"event":"stopping","reason":"exit"}""",
            ],
            events);
    }

    [Theory]
    [InlineData(IpcEventKind.State, "state")]
    [InlineData(IpcEventKind.Frame, "frame")]
    [InlineData(IpcEventKind.Finished, "finished")]
    [InlineData(IpcEventKind.Status, "status")]
    [InlineData(IpcEventKind.Stopping, "stopping")]
    [InlineData(StoppingReason.Shutdown, "shutdown")]
    [InlineData(StoppingReason.Exit, "exit")]
    [InlineData(DictationOutcome.Pasted, "pasted")]
    [InlineData(DictationOutcome.Copied, "copied")]
    [InlineData(DictationOutcome.Empty, "empty")]
    [InlineData(DictationOutcome.Cancelled, "cancelled")]
    [InlineData(DictationOutcome.Error, "error")]
    public void EventValuesKeepTheirWireNames(string value, string wire) => Assert.Equal(wire, value);

    [Fact]
    public void StatesAndModesGoOutUnderTheirEnumNames()
    {
        Assert.Equal(["Idle", "Recording", "Transcribing", "Processing", "Output"], Enum.GetNames<PipelineState>());
        Assert.Equal(["PushToTalk", "Toggle"], Enum.GetNames<HotkeyMode>());
    }

    private static string Serialize(IpcRequest request) => JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest);

    private static string Serialize(IpcResponse response) => JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse);

    private static string Serialize(IpcEvent message) => JsonSerializer.Serialize(message, IpcJsonContext.Default.IpcEvent);
}
