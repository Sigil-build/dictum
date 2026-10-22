using Dictum.Core.Diagnostics;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Shared session-bus access for XDG desktop portals: the Request/Response call pattern, host app
/// registration and interface versions. Messages are built by hand with <see cref="MessageWriter"/>
/// (no code generator, no reflection).
/// </summary>
/// <param name="log">Where host app registration that the portal refuses is logged.</param>
/// <param name="address">The session bus; the one <c>DBUS_SESSION_BUS_ADDRESS</c> names when left out.</param>
public sealed class Portal(ILog log, string? address = null) : IDisposable
{
    public const string Destination = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";

    internal const string SessionInterface = "org.freedesktop.portal.Session";
    internal const string HandleTokenOption = "handle_token";

    // org.freedesktop.portal.Request response codes; the only other one, 2, means any other error.
    internal const uint ResponseSuccess = 0;
    internal const uint ResponseCancelled = 1;

    private readonly SessionBus _bus = new(address);
    private int _token;
    private bool _registered;

    public Task<DBusConnection> ConnectAsync(CancellationToken ct) => _bus.ConnectAsync(ct);

    public void Dispose() => _bus.Dispose();

    /// <summary>A fresh handle_token, unique within this connection.</summary>
    public string NextToken() => $"dictum{Interlocked.Increment(ref _token)}";

    /// <summary>
    /// Tells the portal which app this non-sandboxed process is, so KDE keeps our shortcuts and
    /// permissions under a stable id. Needs xdg-desktop-portal 1.19.3+; older versions refuse, which is logged.
    /// </summary>
    public async Task RegisterHostAppAsync(CancellationToken ct)
    {
        if (_registered)
        {
            return;
        }

        // The portal looks up <AppId>.desktop, so it must exist before registering.
        if (Environment.ProcessPath is { } executable)
        {
            await DesktopEntry.WriteAsync(InstallLocation.Daemon(executable, InstallLocation.AppImage), ct).ConfigureAwait(false);
        }

        var connection = await ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            await connection.CallMethodAsync(BuildRegister(connection)).ConfigureAwait(false);
            log.Debug($"Registered with the portal as {DesktopIdentity.AppId}");
        }
        catch (DBusErrorReplyException ex)
        {
            log.Info($"Host app registration not available: {ex.ErrorName}: {ex.ErrorMessage}");
        }

        _registered = true;
    }

    /// <summary>The <c>version</c> property of a portal interface, or null when the interface is missing.</summary>
    public async Task<uint?> GetVersionAsync(string iface, CancellationToken ct)
    {
        var connection = await ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            return await connection.CallMethodAsync(
                BuildGetProperty(connection, iface, "version"),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).ConfigureAwait(false);
        }
        catch (DBusExceptionBase)
        {
            return null;
        }
    }

    /// <summary>
    /// Calls a portal method that answers through an org.freedesktop.portal.Request object: subscribes
    /// to its Response signal first, then makes the call, then waits for the answer (a dialog can take a while).
    /// </summary>
    public async Task<(uint Code, Dictionary<string, VariantValue> Results)> RequestAsync(
        string token,
        Func<DBusConnection, MessageBuffer> build,
        CancellationToken ct)
    {
        var connection = await ConnectAsync(ct).ConfigureAwait(false);
        var sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var response = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Interface = "org.freedesktop.portal.Request",
                Member = "Response",
                Path = $"{ObjectPath}/request/{sender}/{token}",
            },
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
            },
            notification =>
            {
                if (notification.HasValue)
                {
                    response.TrySetResult(notification.Value);
                }
                else if (notification.Exception is { } exception)
                {
                    response.TrySetException(exception);
                }
            },
            emitOnCapturedContext: false,
            ObserverFlags.None).ConfigureAwait(false);

        await connection.CallMethodAsync(build(connection)).ConfigureAwait(false);
        return await response.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls CreateSession on a portal interface such as GlobalShortcuts or RemoteDesktop. The session handle is null
    /// when the portal refuses; the code says why.
    /// </summary>
    public async Task<(uint Code, string? Session)> CreateSessionAsync(string iface, CancellationToken ct)
    {
        var token = NextToken();
        var sessionToken = NextToken();
        var (code, results) = await RequestAsync(token, c => BuildCreateSession(c, iface, token, sessionToken), ct).ConfigureAwait(false);
        if (code != ResponseSuccess || !results.TryGetValue("session_handle", out var handle))
        {
            return (code, null);
        }

        // The spec types session_handle as 's'; accept an object path too.
        return (code, handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString());
    }

    /// <summary>Closes a portal session (org.freedesktop.portal.Session.Close); errors are ignored.</summary>
    public async Task CloseSessionAsync(string sessionHandle)
    {
        try
        {
            var connection = await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await connection.CallMethodAsync(BuildClose(connection, sessionHandle)).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            // A session the portal no longer knows needs no closing.
        }
    }

    public static bool IsMissing(DBusErrorReplyException ex) =>
        ex.ErrorName is DBusNames.ServiceUnknownError
            or DBusNames.UnknownInterfaceError
            or DBusNames.UnknownMethodError
            or DBusNames.UnknownObjectError;

    private static MessageBuffer BuildRegister(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}");
        writer.WriteString(DesktopIdentity.AppId);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildCreateSession(DBusConnection connection, string iface, string token, string sessionToken)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, iface, "CreateSession", "a{sv}");
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            [HandleTokenOption] = token,
            ["session_handle_token"] = sessionToken,
        });
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetProperty(DBusConnection connection, string iface, string property)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, DBusNames.PropertiesInterface, DBusNames.Get, "ss");
        writer.WriteString(iface);
        writer.WriteString(property);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildClose(DBusConnection connection, string sessionHandle)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, sessionHandle, SessionInterface, "Close", null);
        return writer.CreateMessage();
    }
}
