using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// A session-bus connection of our own, opened on first use and kept open. Not the shared DBusConnection.Session: that
/// one auto-connects and does not allow UniqueName, which the portal Request paths are built from.
/// </summary>
internal sealed class SessionBus : IDisposable
{
    private readonly SemaphoreSlim _connect = new(1, 1);
    private DBusConnection? _connection;

    public async Task<DBusConnection> ConnectAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                var address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
                var connection = new DBusConnection(address);
                try
                {
                    await connection.ConnectAsync().ConfigureAwait(false);
                }
                catch
                {
                    connection.Dispose();
                    throw;
                }

                _connection = connection;
            }

            return _connection;
        }
        finally
        {
            _connect.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connect.Dispose();
    }
}
