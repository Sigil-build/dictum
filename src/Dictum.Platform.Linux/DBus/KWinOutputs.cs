using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>
/// The output KWin calls active, which follows the pointer by default. An X11 window under XWayland cannot ask the X
/// server instead: XWayland sees the pointer only while it is over X11 windows. KWin's output names match Avalonia's.
/// </summary>
public sealed class KWinOutputs : IDisposable
{
    private readonly SemaphoreSlim _connect = new(1, 1);
    private DBusConnection? _connection;

    /// <summary>The output's name, such as "DP-2"; null without a session bus or KWin, or when <paramref name="ct"/> ends first.</summary>
    public async Task<string?> ActiveOutputAsync(CancellationToken ct)
    {
        try
        {
            var connection = await ConnectAsync(ct).ConfigureAwait(false);
            return await connection.CallMethodAsync(Build(connection), static (message, _) => message.GetBodyReader().ReadString())
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A best-effort question: any failure means "no answer", and the overlay goes to the primary screen.
            return null;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connect.Dispose();
    }

    private async Task<DBusConnection> ConnectAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                var address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus.");
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

    private static MessageBuffer Build(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.kde.KWin", "/KWin", "org.kde.KWin", "activeOutputName", null);
        return writer.CreateMessage();
    }
}
