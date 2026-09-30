using System.Buffers.Binary;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;
using Dictum.Platform.Linux;

namespace Dictum.Core.Tests.Linux;

public class WaylandKeystrokesTests
{
    private static readonly SessionInfo Kde = new(SessionType.Wayland, Desktop.Kde, "KDE");
    private static readonly SessionInfo Hyprland = new(SessionType.Wayland, Desktop.Hyprland, "Hyprland");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly List<string> _calls = [];

    [Fact]
    public void AutoPrefersThePortalOnKdeAndWtypeOnWlroots()
    {
        Assert.Equal([InjectorBackend.Portal, InjectorBackend.Ydotool], Create(Kde).Order());
        Assert.Equal([InjectorBackend.Wtype, InjectorBackend.Ydotool, InjectorBackend.Portal], Create(Hyprland).Order());
        Assert.Equal([InjectorBackend.Ydotool], Create(Kde, InjectorBackend.Ydotool).Order());
    }

    [Fact]
    public async Task FallsBackAndSticksWithTheFirstWorkingBackend()
    {
        var sender = Create(Kde, failing: [InjectorBackend.Portal]);

        await sender.SendPasteAsync(PasteKeys.CtrlV, _ct);
        await sender.SendPasteAsync(PasteKeys.CtrlShiftV, _ct);

        Assert.Equal(["portal:CtrlV", "ydotool:CtrlV", "ydotool:CtrlShiftV"], _calls);
        Assert.Equal("ydotool", sender.Backend);
    }

    [Fact]
    public async Task ReportsEveryFailureWhenNothingWorks()
    {
        var sender = Create(Kde, failing: [InjectorBackend.Portal, InjectorBackend.Ydotool]);

        var ex = await Assert.ThrowsAsync<InjectionUnavailableException>(() => sender.SendPasteAsync(PasteKeys.CtrlV, _ct));

        Assert.Contains("portal", ex.Message);
        Assert.Contains("ydotool", ex.Message);
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public void YdotoolEventsUseTheKernelInputEventLayout()
    {
        var bytes = YdotoolKeystrokes.Encode(1, 47, 1);

        Assert.Equal(24, bytes.Length);
        Assert.All(bytes[..16], b => Assert.Equal(0, b));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(47, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20)));
    }

    private WaylandKeystrokes Create(SessionInfo session, InjectorBackend preference = InjectorBackend.Auto, InjectorBackend[]? failing = null) =>
        new(
            session,
            () => preference,
            new Dictionary<InjectorBackend, IKeystrokeSender>
            {
                [InjectorBackend.Portal] = new Recording("portal", _calls, failing?.Contains(InjectorBackend.Portal) == true),
                [InjectorBackend.Ydotool] = new Recording("ydotool", _calls, failing?.Contains(InjectorBackend.Ydotool) == true),
                [InjectorBackend.Wtype] = new Recording("wtype", _calls, failing?.Contains(InjectorBackend.Wtype) == true),
            },
            NullLog.Instance);

    private sealed class Recording(string name, List<string> calls, bool fail) : IKeystrokeSender
    {
        public string Backend => name;

        public Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
        {
            calls.Add($"{name}:{keys}");
            return fail ? Task.FromException(new InjectionUnavailableException($"{name} unavailable")) : Task.CompletedTask;
        }
    }
}
