using Dictum.Core.Diagnostics;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// Desktop notifications that can be clicked, through org.freedesktop.Notifications on a session-bus connection of
/// their own: Notify with a "default" action, and the server's ActionInvoked signal for the click. The connection opens on
/// the first notification and stays open, so a click can still come back while the notification waits in the
/// desktop's list.
/// </summary>
/// <param name="address">The session bus; the one <c>DBUS_SESSION_BUS_ADDRESS</c> names when left out.</param>
public sealed class DesktopNotifications(ILog log, string? address = null) : IDisposable
{
    public const string Service = "org.freedesktop.Notifications";
    public const string ObjectPath = "/org/freedesktop/Notifications";
    public const string Interface = "org.freedesktop.Notifications";

    /// <summary>The action a server runs when the notification itself is clicked.</summary>
    public const string DefaultAction = "default";

    private const byte NormalUrgency = 1;

    /// <summary>The server's own timeout.</summary>
    private const int ServerTimeout = -1;

    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Action> _pending = [];
    private DBusConnection? _connection;
    private IDisposable? _invoked;
    private IDisposable? _closed;

    /// <summary>
    /// Shows <paramref name="summary"/> and <paramref name="body"/>; <paramref name="clicked"/> runs on a thread-pool
    /// thread when the user clicks it. False when there is no session bus or no notification server. Never throws.
    /// </summary>
    /// <param name="action">The label a server that draws buttons puts on the click.</param>
    public async Task<bool> ShowAsync(string summary, string body, string action, Action clicked, CancellationToken ct)
    {
        try
        {
            var connection = await ConnectAsync(ct).ConfigureAwait(false);
            var id = await connection.CallMethodAsync(
                Notify(connection, summary, body, action),
                static (message, _) => message.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
            lock (_gate)
            {
                _pending[id] = clicked;
            }

            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException or IOException)
        {
            log.Info($"Notification not shown: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _invoked?.Dispose();
        _closed?.Dispose();
        _connection?.Dispose();
        _connect.Dispose();
    }

    private async Task<DBusConnection> ConnectAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var bus = address ?? DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
            var connection = new DBusConnection(bus);
            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);

                // Listen before the first Notify, so no click can come before the subscription.
                _invoked = await connection.AddMatchAsync(
                    Signal("ActionInvoked"),
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Id: reader.ReadUInt32(), Action: reader.ReadString());
                    },
                    notification =>
                    {
                        if (notification.HasValue && notification.Value.Action == DefaultAction && Take(notification.Value.Id) is { } click)
                        {
                            Run(click);
                        }
                    },
                    emitOnCapturedContext: false,
                    ObserverFlags.None).ConfigureAwait(false);
                _closed = await connection.AddMatchAsync(
                    Signal("NotificationClosed"),
                    static (message, _) => message.GetBodyReader().ReadUInt32(),
                    notification =>
                    {
                        if (notification.HasValue)
                        {
                            Take(notification.Value);
                        }
                    },
                    emitOnCapturedContext: false,
                    ObserverFlags.None).ConfigureAwait(false);
            }
            catch
            {
                _invoked?.Dispose();
                _closed?.Dispose();
                connection.Dispose();
                throw;
            }

            _connection = connection;
            return connection;
        }
        finally
        {
            _connect.Release();
        }
    }

    private static MatchRule Signal(string member) =>
        new() { Type = MessageType.Signal, Interface = Interface, Member = member, Path = ObjectPath };

    private Action? Take(uint id)
    {
        lock (_gate)
        {
            return _pending.Remove(id, out var click) ? click : null;
        }
    }

    private void Run(Action click)
    {
        try
        {
            click();
        }
        catch (Exception ex)
        {
            // A handler that throws must not close the connection the next notifications use.
            log.Warn($"The notification's click failed: {ex.Message}");
        }
    }

    private static MessageBuffer Notify(DBusConnection connection, string summary, string body, string action)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ObjectPath, Interface, "Notify", "susssasa{sv}i");
        writer.WriteString(DesktopIdentity.Name);
        writer.WriteUInt32(0);
        writer.WriteString(DesktopIdentity.Icon);
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(new[] { DefaultAction, action });
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["urgency"] = NormalUrgency,
            ["desktop-entry"] = DesktopIdentity.AppId,
        });
        writer.WriteInt32(ServerTimeout);
        return writer.CreateMessage();
    }
}
