using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.Tray;

public enum TrayAvailability { Unknown, NoWatcher, NoHost, Registered, Failed }

/// <summary>Owns the tray's own D-Bus connection, the bus name, the exported objects and the watcher registration.</summary>
public sealed class StatusNotifierHost : IAsyncDisposable
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";

    private readonly DBusConnection _connection;
    private readonly ILog _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _watch;

    private StatusNotifierHost(DBusConnection connection, string busName, ILog log)
    {
        _connection = connection;
        _log = log;
        BusName = busName;
        Menu = new DBusMenu(connection);
        Item = new StatusNotifierItem(connection, DBusMenu.MenuPath);
    }

    public string BusName { get; }
    public StatusNotifierItem Item { get; }
    public DBusMenu Menu { get; }

    /// <summary>Raised (on a thread-pool thread) whenever the tray becomes usable or unusable.</summary>
    public event Action<TrayAvailability>? AvailabilityChanged;

    public TrayAvailability Availability { get; private set; }

    /// <summary>The tray's own connection, name, objects and watcher registration; null without a session bus.</summary>
    public static async Task<StatusNotifierHost?> TryStartAsync(ILog log)
    {
        if (DBusAddress.Session is not { } address)
        {
            log.Warn("No D-Bus session bus; no tray icon");
            return null;
        }

        // A dedicated connection: AddMethodHandler and RequestNameAsync throw on AutoConnect connections, and a handler
        // failure must not take down the portal connection the hotkey and paste sessions live on.
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);

            // Own the name before exporting: a host that scans the bus would otherwise show a second item.
            var busName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
            await connection.RequestNameAsync(busName, RequestNameOptions.None).ConfigureAwait(false);
            var host = new StatusNotifierHost(connection, busName, log);
            connection.AddMethodHandlers([host.Item, host.Menu]);
            host._watch = host.WatchWatcherAsync(host._stop.Token);
            return host;
        }
        catch (DBusExceptionBase ex)
        {
            log.Warn($"Could not put the tray icon on the session bus: {ex.Message}");
            connection.Dispose();
            return null;
        }
    }

    private async Task WatchWatcherAsync(CancellationToken ct)
    {
        try
        {
            using var watcher = await _connection.WatchNameOwnerAsync(WatcherName).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var owner = watcher.GetCurrentOwner();
                if (owner is null)
                {
                    // GNOME without the AppIndicator extension, or Plasma's kded6 not (yet) running.
                    Report(TrayAvailability.NoWatcher);
                    owner = await watcher.WaitForOwnerAsync(ct).ConfigureAwait(false);
                }

                // A new watcher knows nothing about us (Plasma's watcher keeps registrations in memory only).
                Report(await RegisterAsync(owner).ConfigureAwait(false));

                using var changed = CancellationTokenSource.CreateLinkedTokenSource(watcher.GetOwnerChangedCancellationToken(owner), ct);
                try
                {
                    await Task.Delay(Timeout.Infinite, changed.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Owner changed: loop and register with the new watcher.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Warn($"The tray icon lost its watcher: {ex.Message}");
            Report(TrayAvailability.Failed);
        }
    }

    private void Report(TrayAvailability availability)
    {
        Availability = availability;
        if (AvailabilityChanged is { } changed)
        {
            ThreadPool.QueueUserWorkItem(_ => changed(availability));
        }
    }

    private async Task<TrayAvailability> RegisterAsync(string watcherOwner)
    {
        try
        {
            // The owner identifier pins the call to this watcher instance.
            await _connection.CallMethodAsync(BuildRegister(watcherOwner)).ConfigureAwait(false);
            var hostRegistered = await _connection.CallMethodAsync(
                BuildGetHostRegistered(watcherOwner),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetBool()).ConfigureAwait(false);
            // Plasma's and GNOME-extension's watchers always answer true; other watchers may not.
            return hostRegistered ? TrayAvailability.Registered : TrayAvailability.NoHost;
        }
        catch (DBusExceptionBase)
        {
            return TrayAvailability.Failed;
        }
    }

    private MessageBuffer BuildRegister(string destination)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, WatcherPath, WatcherInterface, "RegisterStatusNotifierItem", "s");
        writer.WriteString(BusName);
        return writer.CreateMessage();
    }

    private MessageBuffer BuildGetHostRegistered(string destination)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, WatcherPath, DBusNames.PropertiesInterface, DBusNames.Get, "ss");
        writer.WriteString(WatcherInterface);
        writer.WriteString("IsStatusNotifierHostRegistered");
        return writer.CreateMessage();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_watch is { } watch)
        {
            await watch.ConfigureAwait(false);
        }

        // Dropping the connection releases the name; watchers then emit StatusNotifierItemUnregistered.
        _connection.Dispose();
        _stop.Dispose();
    }
}
