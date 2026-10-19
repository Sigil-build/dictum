using Dictum.Core.Diagnostics;
using Dictum.Platform.Injection;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Sends the paste keystroke through org.freedesktop.portal.RemoteDesktop: the desktop asks once
/// whether Dictum may control the keyboard, and a restore token (kept in the data directory) lets it
/// reconnect later without asking again. Needs no root and no extra daemon.
/// </summary>
public sealed class PortalKeystrokes(Portal portal, string restoreTokenFile, ILog log) : IKeystrokeSender, IPreparable
{
    public const string Interface = "org.freedesktop.portal.RemoteDesktop";

    private const uint DeviceKeyboard = 1;
    private const uint PersistUntilRevoked = 2;

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
            await Task.Delay(8, ct).ConfigureAwait(false);
        }

        foreach (var code in codes.Reverse())
        {
            await connection.CallMethodAsync(BuildKey(connection, session, code, pressed: false)).ConfigureAwait(false);
            await Task.Delay(8, ct).ConfigureAwait(false);
        }
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

            var token = portal.NextToken();
            var sessionToken = portal.NextToken();
            var (code, results) = await portal.RequestAsync(token, c => BuildCreateSession(c, token, sessionToken), ct).ConfigureAwait(false);
            if (code != 0 || !results.TryGetValue("session_handle", out var handle))
            {
                throw new InjectionUnavailableException($"The RemoteDesktop portal refused to create a session (code {code}).");
            }

            var session = handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();
            var restore = File.Exists(restoreTokenFile) ? (await File.ReadAllTextAsync(restoreTokenFile, ct).ConfigureAwait(false)).Trim() : null;

            var selectToken = portal.NextToken();
            (code, _) = await portal.RequestAsync(selectToken, c => BuildSelectDevices(c, session, selectToken, restore), ct).ConfigureAwait(false);
            if (code != 0)
            {
                throw Denied(code);
            }

            var startToken = portal.NextToken();
            (code, results) = await portal.RequestAsync(startToken, c => BuildStart(c, session, startToken), ct).ConfigureAwait(false);
            if (code != 0)
            {
                throw Denied(code);
            }

            if (results.TryGetValue("devices", out var devices) && (devices.GetUInt32() & DeviceKeyboard) == 0)
            {
                throw new InjectionUnavailableException("Keyboard control was not granted.");
            }

            if (results.TryGetValue("restore_token", out var newToken))
            {
                await SaveTokenAsync(newToken.GetString(), ct).ConfigureAwait(false);
            }

            _closed?.Dispose();
            _closed = await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.portal.Session", Member = "Closed", Path = session },
                static (_, _) => true,
                _ =>
                {
                    log.Info("RemoteDesktop session closed; it will be reopened on the next paste");
                    _session = null;
                },
                emitOnCapturedContext: false,
                ObserverFlags.None).ConfigureAwait(false);

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

    private static InjectionUnavailableException Denied(uint code) => code == 1
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

    private static MessageBuffer BuildCreateSession(DBusConnection connection, string token, string sessionToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "CreateSession", "a{sv}");
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["session_handle_token"] = sessionToken,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSelectDevices(DBusConnection connection, string session, string token, string? restoreToken)
    {
        var options = new Dictionary<string, VariantValue>
        {
            ["handle_token"] = token,
            ["types"] = VariantValue.UInt32(DeviceKeyboard),
            ["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked),
        };
        if (!string.IsNullOrEmpty(restoreToken))
        {
            options["restore_token"] = restoreToken;
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
        writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildKey(DBusConnection connection, string session, ushort keycode, bool pressed)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal.Destination, Portal.ObjectPath, Interface, "NotifyKeyboardKeycode", "oa{sv}iu");
        writer.WriteObjectPath(session);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        writer.WriteInt32(keycode);
        writer.WriteUInt32(pressed ? 1u : 0u);
        return writer.CreateMessage();
    }
}
