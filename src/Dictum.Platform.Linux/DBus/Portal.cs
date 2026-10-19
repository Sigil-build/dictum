using Dictum.Core.Diagnostics;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Shared session-bus access for XDG desktop portals: the Request/Response call pattern, host app
/// registration and interface versions. Messages are built by hand with <see cref="MessageWriter"/>
/// (no code generator, no reflection).
/// </summary>
public sealed class Portal
{
    public const string Destination = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string AppId = "dev.dictum.Dictum";

    private readonly ILog _log;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private DBusConnection? _connection;
    private int _token;
    private bool _registered;

    public Portal(ILog log) => _log = log;

    public async Task<DBusConnection> ConnectAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                // Our own connection, not the shared DBusConnection.Session: that one auto-connects and does not
                // allow UniqueName, which the portal Request paths are built from.
                var address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
                var connection = new DBusConnection(address);
                await connection.ConnectAsync().ConfigureAwait(false);
                _connection = connection;
            }

            return _connection;
        }
        finally
        {
            _connect.Release();
        }
    }

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
            _log.Debug($"Registered with the portal as {AppId}");
        }
        catch (DBusErrorReplyException ex)
        {
            _log.Info($"Host app registration not available: {ex.ErrorName}: {ex.ErrorMessage}");
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
    /// The response code is 0 for success, 1 when the dialog was cancelled and 2 for any other error.
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
        }
    }

    public static bool IsMissing(DBusErrorReplyException ex) =>
        ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.UnknownInterface"
            or "org.freedesktop.DBus.Error.UnknownMethod"
            or "org.freedesktop.DBus.Error.UnknownObject";

    private static MessageBuffer BuildRegister(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}");
        writer.WriteString(AppId);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetProperty(DBusConnection connection, string iface, string property)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss");
        writer.WriteString(iface);
        writer.WriteString(property);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildClose(DBusConnection connection, string sessionHandle)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Destination, sessionHandle, "org.freedesktop.portal.Session", "Close", null);
        return writer.CreateMessage();
    }
}
