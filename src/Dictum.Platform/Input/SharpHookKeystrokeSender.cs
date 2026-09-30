using Dictum.Platform.Injection;
using SharpHook.Data;
using SharpHook.Simulation;

namespace Dictum.Platform.Input;

/// <summary>Paste keystroke via SharpHook: SendInput on Windows, XTest on Linux X11.</summary>
public sealed class SharpHookKeystrokeSender(string backend) : IKeystrokeSender, IDisposable
{
    private readonly Lazy<EventSimulator> _simulator = new(() => EventSimulator.Create("dictum"));

    public string Backend => backend;

    public Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
    {
        KeyCode[] stroke = keys == PasteKeys.CtrlShiftV
            ? [KeyCode.VcLeftControl, KeyCode.VcLeftShift, KeyCode.VcV]
            : [KeyCode.VcLeftControl, KeyCode.VcV];
        _simulator.Value.SimulateKeyStroke(stroke);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_simulator.IsValueCreated)
        {
            _simulator.Value.Dispose();
        }
    }
}
