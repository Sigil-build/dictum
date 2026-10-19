using Dictum.Core.Diagnostics;
using Dictum.Platform.Linux.Tray;
using Tmds.DBus.Protocol;

namespace Dictum.Integration.Tests.Linux;

/// <summary>
/// Our StatusNotifierItem and dbusmenu on a real session bus, with this class as the watcher and the host. Linux only;
/// CI runs it inside <c>dbus-run-session</c>.
/// </summary>
public sealed class StatusNotifierTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheItemRegistersAndAnswersGetAll()
    {
        SkipWithoutBus();
        await using var watcher = await FakeWatcher.StartAsync();
        await using var host = (await StatusNotifierHost.TryStartAsync(NullLog.Instance))!;
        host.Item.Update(new SniState(SniStatus.NeedsAttention, "dictum-recording", "dictum-recording", "/tmp/dictum-icons", "Dictum", "Dictum", "Dictum · Recording"));

        var registered = await watcher.Registered.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);
        using var client = await ConnectAsync();
        var properties = await GetAllAsync(client, host.BusName, "/StatusNotifierItem", "org.kde.StatusNotifierItem");

        Assert.Equal(host.BusName, registered);
        Assert.Equal("dictum-recording", properties["IconName"].GetString());
        Assert.Equal(SniStatus.NeedsAttention, properties["Status"].GetString());
        Assert.Equal("/tmp/dictum-icons", properties["IconThemePath"].GetString());
        Assert.False(properties["ItemIsMenu"].GetBool());
        Assert.Equal(TrayAvailability.Registered, host.Availability);
    }

    [Fact]
    public async Task TheMenuLayoutAndAClickTravelOverTheBus()
    {
        SkipWithoutBus();
        await using var watcher = await FakeWatcher.StartAsync();
        await using var host = (await StatusNotifierHost.TryStartAsync(NullLog.Instance))!;
        var clicked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Menu.Clicked += id => clicked.TrySetResult(id);
        host.Menu.SetMenu(new MenuNode(0, Children: [new MenuNode(1001, "uk", ToggleType: MenuNode.Checkmark, ToggleState: 1), new MenuNode(1002, Separator: true), new MenuNode(1003, "Quit Dictum")]));
        using var client = await ConnectAsync();

        var (ids, labels) = await GetLayoutAsync(client, host.BusName);
        await ClickAsync(client, host.BusName, 1003);

        Assert.Equal([1001, 1002, 1003], ids);
        Assert.Equal(["uk", "", "Quit Dictum"], labels);
        Assert.Equal(1003, await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct));
    }

    [Fact]
    public async Task ANewWatcherGetsTheItemAgain()
    {
        SkipWithoutBus();
        var first = await FakeWatcher.StartAsync();
        await using var host = (await StatusNotifierHost.TryStartAsync(NullLog.Instance))!;
        await first.Registered.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct);

        await first.DisposeAsync();
        await using var second = await FakeWatcher.StartAsync();

        Assert.Equal(host.BusName, await second.Registered.Task.WaitAsync(TimeSpan.FromSeconds(10), _ct));
    }

    [Fact]
    public async Task NoWatcherIsReported()
    {
        SkipWithoutBus();
        await using var host = (await StatusNotifierHost.TryStartAsync(NullLog.Instance))!;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (host.Availability != TrayAvailability.NoWatcher && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, _ct);
        }

        Assert.Equal(TrayAvailability.NoWatcher, host.Availability);
    }

    private static void SkipWithoutBus()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")), "No session bus; run inside dbus-run-session");
    }

    private static async Task<DBusConnection> ConnectAsync()
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        return connection;
    }

    private static Task<Dictionary<string, VariantValue>> GetAllAsync(DBusConnection client, string destination, string path, string @interface)
    {
        using var writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, path, "org.freedesktop.DBus.Properties", "GetAll", "s");
        writer.WriteString(@interface);
        return client.CallMethodAsync(writer.CreateMessage(), static (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue());
    }

    private static Task<(List<int> Ids, List<string> Labels)> GetLayoutAsync(DBusConnection client, string destination)
    {
        using var writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, "/MenuBar", "com.canonical.dbusmenu", "GetLayout", "iias");
        writer.WriteInt32(0);
        writer.WriteInt32(-1);
        writer.WriteArray(Array.Empty<string>());
        return client.CallMethodAsync(writer.CreateMessage(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            reader.ReadUInt32();
            reader.AlignStruct();
            reader.ReadInt32();
            reader.ReadDictionaryOfStringToVariantValue();
            var ids = new List<int>();
            var labels = new List<string>();
            var end = reader.ReadArrayStart(DBusType.Variant);
            while (reader.HasNext(end))
            {
                var child = reader.ReadVariantValue();
                ids.Add(child.GetItem(0).GetInt32());
                var properties = child.GetItem(1).GetDictionary<string, VariantValue>();
                labels.Add(properties.TryGetValue("label", out var label) ? label.GetString() : "");
            }

            return (ids, labels);
        });
    }

    /// <summary>What Plasma sends for a click: Event(id, "clicked", &lt;s ""&gt;, 0).</summary>
    private static Task ClickAsync(DBusConnection client, string destination, int id)
    {
        using var writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, "/MenuBar", "com.canonical.dbusmenu", "Event", "isvu");
        writer.WriteInt32(id);
        writer.WriteString("clicked");
        writer.WriteVariantString("");
        writer.WriteUInt32(0);
        return client.CallMethodAsync(writer.CreateMessage());
    }

    /// <summary>A minimal org.kde.StatusNotifierWatcher on its own connection.</summary>
    private sealed class FakeWatcher : IPathMethodHandler, IAsyncDisposable
    {
        private DBusConnection? _connection;

        public TaskCompletionSource<string> Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Path => "/StatusNotifierWatcher";

        public bool HandlesChildPaths => false;

        public static async Task<FakeWatcher> StartAsync()
        {
            var watcher = new FakeWatcher { _connection = await ConnectAsync() };
            watcher._connection.AddMethodHandler(watcher);
            await watcher._connection.RequestNameAsync("org.kde.StatusNotifierWatcher", RequestNameOptions.None);
            return watcher;
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            switch ((request.InterfaceAsString, request.MemberAsString, request.SignatureAsString))
            {
                case ("org.kde.StatusNotifierWatcher", "RegisterStatusNotifierItem", "s"):
                    Registered.TrySetResult(request.GetBodyReader().ReadString());
                    using (var writer = context.CreateReplyWriter(null))
                    {
                        context.Reply(writer.CreateMessage());
                    }

                    break;
                case ("org.freedesktop.DBus.Properties", "Get", "ss"):
                    using (var writer = context.CreateReplyWriter("v"))
                    {
                        writer.WriteVariantBool(true);
                        context.Reply(writer.CreateMessage());
                    }

                    break;
            }

            return default;
        }

        public ValueTask DisposeAsync()
        {
            // Closing the connection drops the name, as a watcher restart does.
            _connection?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
