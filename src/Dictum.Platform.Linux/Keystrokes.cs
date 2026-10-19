using System.Buffers.Binary;
using System.Net.Sockets;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;

namespace Dictum.Platform.Linux;

/// <summary>Linux evdev key codes used for the paste shortcut, and the pause after each of its key events.</summary>
internal static class EvdevKeys
{
    public const ushort LeftCtrl = 29;
    public const ushort LeftShift = 42;
    public const ushort V = 47;

    public static readonly TimeSpan KeyEventGap = TimeSpan.FromMilliseconds(8);

    public static ushort[] For(PasteKeys keys) =>
        keys == PasteKeys.CtrlShiftV ? [LeftCtrl, LeftShift, V] : [LeftCtrl, V];
}

/// <summary>
/// Writes key events straight to the ydotoold socket (the same datagrams the ydotool client sends),
/// so no process is spawned per paste. Needs ydotoold running with access to /dev/uinput.
/// </summary>
public sealed class YdotoolKeystrokes : IKeystrokeSender
{
    private const ushort EvSyn = 0;
    private const ushort EvKey = 1;
    private const ushort SynReport = 0;
    private const int KeyReleased = 0;
    private const int KeyPressed = 1;

    private const int InputEventSize = 24;
    private const int TypeOffset = 16;
    private const int CodeOffset = 18;
    private const int ValueOffset = 20;

    private const string SocketName = ".ydotool_socket";
    private const string FallbackSocket = "/tmp/" + SocketName;

    public string Backend => "ydotool";

    public static string SocketPath =>
        Environment.GetEnvironmentVariable("YDOTOOL_SOCKET") is { Length: > 0 } custom ? custom
        : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && File.Exists(Path.Combine(runtime, SocketName))
            ? Path.Combine(runtime, SocketName)
            : FallbackSocket;

    public static bool IsAvailable => File.Exists(SocketPath);

    public async Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InjectionUnavailableException(
                $"ydotoold is not running ({SocketPath} is missing).",
                "Install ydotool and start it: 'systemctl --user enable --now ydotool' (needs access to /dev/uinput).");
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct).ConfigureAwait(false);
        var codes = EvdevKeys.For(keys);
        foreach (var code in codes)
        {
            await SendKeyAsync(socket, code, KeyPressed, ct).ConfigureAwait(false);
        }

        foreach (var code in codes.Reverse())
        {
            await SendKeyAsync(socket, code, KeyReleased, ct).ConfigureAwait(false);
        }
    }

    /// <summary>struct input_event on 64-bit Linux: timeval (16 bytes), u16 type, u16 code, s32 value.</summary>
    public static byte[] Encode(ushort type, ushort code, int value)
    {
        var buffer = new byte[InputEventSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(TypeOffset), type);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(CodeOffset), code);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ValueOffset), value);
        return buffer;
    }

    private static async Task SendKeyAsync(Socket socket, ushort code, int value, CancellationToken ct)
    {
        await socket.SendAsync(Encode(EvKey, code, value), SocketFlags.None, ct).ConfigureAwait(false);
        await socket.SendAsync(Encode(EvSyn, SynReport, 0), SocketFlags.None, ct).ConfigureAwait(false);
        await Task.Delay(EvdevKeys.KeyEventGap, ct).ConfigureAwait(false);
    }
}

/// <summary>wtype, for wlroots compositors (Hyprland, Sway) that offer the virtual-keyboard protocol.</summary>
public sealed class WtypeKeystrokes : IKeystrokeSender
{
    private const string Program = "wtype";

    public string Backend => Program;

    public static bool IsAvailable => Tools.Exists(Program);

    public async Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
    {
        string[] args = keys == PasteKeys.CtrlShiftV
            ? ["-M", "ctrl", "-M", "shift", "-k", "v", "-m", "shift", "-m", "ctrl"]
            : ["-M", "ctrl", "-k", "v", "-m", "ctrl"];
        var result = await Tools.RunAsync(Program, args, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InjectionUnavailableException($"wtype failed: {result.Error.Trim()}");
        }
    }
}

/// <summary>
/// Tries the Wayland paste backends in order (from <c>general.injector</c> and the desktop) and sticks
/// with the first one that works.
/// </summary>
public sealed class WaylandKeystrokes(
    SessionInfo session,
    Func<InjectorBackend> preference,
    IReadOnlyDictionary<InjectorBackend, IKeystrokeSender> backends,
    ILog log) : IKeystrokeSender, IPreparable
{
    private IKeystrokeSender? _working;

    public string Backend => _working?.Backend ?? string.Join(" → ", Order().Select(b => backends[b].Backend));

    public IReadOnlyList<InjectorBackend> Order() => preference() switch
    {
        InjectorBackend.Auto when session.IsWlroots => [InjectorBackend.Wtype, InjectorBackend.Ydotool, InjectorBackend.Portal],
        InjectorBackend.Auto => [InjectorBackend.Portal, InjectorBackend.Ydotool],
        var only => [only],
    };

    public async Task PrepareAsync(CancellationToken ct)
    {
        if (backends[Order()[0]] is IPreparable first)
        {
            await first.PrepareAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
    {
        if (_working is { } working)
        {
            try
            {
                await working.SendPasteAsync(keys, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsBackendFailure(ex))
            {
                log.Warn($"{working.Backend} stopped working: {ex.Message}");
                _working = null;
            }
        }

        var failures = new List<string>();
        foreach (var backend in Order().Select(b => backends[b]))
        {
            try
            {
                await backend.SendPasteAsync(keys, ct).ConfigureAwait(false);
                _working = backend;
                log.Info($"Pasting with {backend.Backend}");
                return;
            }
            catch (Exception ex) when (IsBackendFailure(ex))
            {
                failures.Add($"{backend.Backend}: {ex.Message}");
            }
        }

        throw new InjectionUnavailableException(
            $"No way to send Ctrl+V; the text is on the clipboard. {string.Join(" ", failures)}",
            "Allow keyboard control when the desktop asks, or set up ydotool; 'dictum doctor' shows what is missing.");
    }

    private static bool IsBackendFailure(Exception ex) =>
        ex is InjectionUnavailableException or SocketException or TimeoutException or InvalidOperationException
            or System.ComponentModel.Win32Exception or Tmds.DBus.Protocol.DBusExceptionBase;
}
