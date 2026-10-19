using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Sends the paste keystroke through org.freedesktop.portal.RemoteDesktop: the desktop asks once
/// whether Dictum may control the keyboard, and a restore token (kept in the data directory) lets it
/// reconnect later without asking again. Needs no root and no extra daemon.
/// </summary>
public sealed class PortalKeystrokes(Portal portal, string restoreTokenFile, ILog log) : IKeystrokeSender, IPreparable, IDisposable
{
    public const string Interface = "org.freedesktop.portal.RemoteDesktop";

    private const uint DeviceKeyboard = 1;
    private const uint PersistUntilRevoked = 2;
    private const uint KeyReleased = 0;
    private const uint KeyPressed = 1;
    private const string RestoreTokenKey = "restore_token";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _session;
    private IDisposable? _closed;

    public string Backend => "RemoteDesktop portal";

    public Task PrepareAsync(CancellationToken ct) => EnsureSessionAsync(ct);

    public async Task SendPasteAsync(PasteKeys keys, CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
        var connection = await portal.ConnectAsync(ct).ConfigureAwait(false);
        var codes = EvdevKeys.For(keys);
        foreach (var code in codes)
        {
            await connection.CallMethodAsync(BuildKey(connection, session, code, pressed: true)).ConfigureAwait(false);
            await Task.Delay(EvdevKeys.KeyEventGap, ct).ConfigureAwait(false);
        }

        foreach (var code in codes.Reverse())
        {
            await connection.CallMethodAsync(BuildKey(connection, session, code, pressed: false)).ConfigureAwait(false);
            await Task.Delay(EvdevKeys.KeyEventGap, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _closed?.Dispose();
        _gate.Dispose();
    }

    private async Task<string> EnsureSessionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is { } existing)
            {
                return existing;
            }

            var connection = await portal.ConnectAsync(ct).ConfigureAwait(false);
            await portal.RegisterHostAppAsync(ct).ConfigureAwait(false);
            if (await portal.GetVersionAsync(Interface, ct).ConfigureAwait(false) is null)
            {
                throw new InjectionUnavailableException("This desktop has no RemoteDesktop portal.");
            }

            var (code, session) = await portal.CreateSessionAsync(Interface, ct).ConfigureAwait(false);
            if (session is null)
            {
                throw new InjectionUnavailableException($"The RemoteDesktop portal refused to create a session (code {code}).");
            }

            var restore = File.Exists(restoreTokenFile) ? (await File.ReadAllTextAsync(restoreTokenFile, ct).ConfigureAwait(false)).Trim() : null;
            await StartAsync(session, restore, ct).ConfigureAwait(false);
            await WatchClosedAsync(connection, session).ConfigureAwait(false);

            log.Info($"RemoteDesktop keyboard access granted{(restore is null ? "" : " (restored)")}");
            _session = session;
            return session;
        }
        catch (DBusErrorReplyException ex) when (Portal.IsMissing(ex))
        {
            throw new InjectionUnavailableException($"The RemoteDesktop portal is not available ({ex.ErrorName}).", inner: ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Selects the keyboard and starts the session, which shows the dialog unless the restore token still holds.</summary>
    private async Task StartAsync(string session, string? restoreToken, CancellationToken ct)
    {
        var selectToken = portal.NextToken();
        var (selected, _) = await portal.RequestAsync(selectToken, c => BuildSelectDevices(c, session, selectToken, restoreToken), ct).ConfigureAwait(false);
        if (selected != Portal.ResponseSuccess)
        {
            throw Denied(selected);
        }

        var startToken = portal.NextToken();
        var (started, results) = await portal.RequestAsync(startToken, c => BuildStart(c, session, startToken), ct).ConfigureAwait(false);
        if (started != Portal.ResponseSuccess)
        {
            throw Denied(started);
        }

        if (results.TryGetValue("devices", out var devices) && (devices.GetUInt32() & DeviceKeyboard) == 0)
        {
            throw new InjectionUnavailableException("Keyboard control was not granted.");
        }

        if (results.TryGetValue(RestoreTokenKey, out var newToken))
        {
            await SaveTokenAsync(newToken.GetString(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Forgets the session when the desktop closes it, so the next paste opens a new one.</summary>
    private async Task WatchClosedAsync(DBusConnection connection, string session)
    {
        _closed?.Dispose();
        _closed = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Interface = Portal.SessionInterface, Member = "Closed", Path = session },
            static (_, _) => true,
            _ =>
            {
                log.Info("RemoteDesktop session closed; it will be reopened on the next paste");
                _session = null;
            },
            emitOnCapturedContext: false,
            ObserverFlags.None).ConfigureAwait(false);
    }

    private static InjectionUnavailableException Denied(uint code) => code == Portal.ResponseCancelled
        ? new("Keyboard control was not allowed in the desktop dialog.", "Restart 'dictum daemon' and allow it, or use ydotool (\"injector\": \"ydotool\").")
        : new($"The RemoteDesktop portal failed (code {code}).");

    private async Task SaveTokenAsync(string token, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(restoreTokenFile)!);
        await File.WriteAllTextAsync(restoreTokenFile, token, ct).ConfigureAwait(false);

        // The token restores keyboard control without the dialog, so only its owner may read it.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(restoreTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static MessageBuffer BuildSelectDevices(DBusConnection connection, string session, string token, string? restoreToken)
    {
        var options = new Dictionary<string, VariantValue>
        {
            [Portal.HandleTokenOption] = token,
            ["types"] = VariantValue.UInt32(DeviceKeyboard),
            ["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked),
        };
        if (!string.IsNullOrEmpty(restoreToken))
        {
            options[RestoreTokenKey] = restoreToken;
        }

        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "SelectDevices", "oa{sv}");
        writer.WriteObjectPath(session);
        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildStart(DBusConnection connection, string session, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "Start", "osa{sv}");
        writer.WriteObjectPath(session);
        writer.WriteString(string.Empty);
        writer.WriteDictionary(new Dictionary<string, VariantValue> { [Portal.HandleTokenOption] = token });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildKey(DBusConnection connection, string session, ushort keycode, bool pressed)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "NotifyKeyboardKeycode", "oa{sv}iu");
        writer.WriteObjectPath(session);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        writer.WriteInt32(keycode);
        writer.WriteUInt32(pressed ? KeyPressed : KeyReleased);
        return writer.CreateMessage();
    }
}
