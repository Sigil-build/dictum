using Dictum.Platform.Linux.DBus;
using Dictum.Testing;
using Tmds.DBus.Protocol;

namespace Dictum.Integration.Tests.Linux;

/// <summary>
/// The update notification on a real session bus, with this class as the notification server. Linux only; CI runs it
/// inside <c>dbus-run-session</c>.
/// </summary>
public sealed class DesktopNotificationsTests
{
    private const uint ServerId = 42;

    /// <summary>For a round trip over the session bus.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheNotificationCarriesItsTextAndADefaultActionAndItsClickComesBack()
    {
        SkipWithoutBus();
        await using var server = await FakeServer.StartAsync();
        var log = new ListLog();
        using var notifications = new DesktopNotifications(log);
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var shown = await notifications.ShowAsync("Dictum 0.5.0 is available", "Click to open the release page.", "Open the release page",
            () => clicked.TrySetResult(), _ct);
        var notify = await server.Notified.Task.WaitAsync(Patience, _ct);
        server.Invoke(ServerId, "default");

        Assert.True(shown, string.Join('\n', log.Messages));
        Assert.Equal(("Dictum", "Dictum 0.5.0 is available", "Click to open the release page."), (notify.App, notify.Summary, notify.Body));
        Assert.Equal(["default", "Open the release page"], notify.Actions);
        Assert.Equal("dev.dictum.Dictum", notify.DesktopEntry);
        await clicked.Task.WaitAsync(Patience, _ct);
    }

    // Another notification's action, or one closed first, runs nothing.
    [Fact]
    public async Task OnlyTheDefaultActionOfThisNotificationRunsTheClick()
    {
        SkipWithoutBus();
        await using var server = await FakeServer.StartAsync();
        using var notifications = new DesktopNotifications(new ListLog());
        var clicks = 0;

        await notifications.ShowAsync("Dictum 0.5.0 is available", "Click to open the release page.", "Open", () => Interlocked.Increment(ref clicks), _ct);
        await server.Notified.Task.WaitAsync(Patience, _ct);
        server.Invoke(ServerId + 1, "default");
        server.Invoke(ServerId, "settings");
        server.Close(ServerId);
        server.Invoke(ServerId, "default");
        await Task.Delay(TimeSpan.FromMilliseconds(500), _ct);

        Assert.Equal(0, Volatile.Read(ref clicks));
    }

    // No bus to reach, as on a desktop without D-Bus: the tray and About Dictum still say it; nothing throws.
    [Fact]
    public async Task WithoutABusItSaysSoAndDoesNotThrow()
    {
        var log = new ListLog();
        using var notifications = new DesktopNotifications(log, $"unix:path={Path.Combine(Path.GetTempPath(), $"no-bus-{Guid.NewGuid():N}")}");

        var shown = await notifications.ShowAsync("Dictum 0.5.0 is available", "Body", "Open", () => { }, _ct);

        Assert.False(shown);
        Assert.True(log.Has("Notification not shown"));
    }

    private static void SkipWithoutBus()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only");
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")), "No session bus; run inside dbus-run-session");
    }

    private sealed record Notify(string App, string Summary, string Body, List<string> Actions, string? DesktopEntry);

    /// <summary>A minimal org.freedesktop.Notifications on its own connection, which answers Notify with <see cref="ServerId"/>.</summary>
    private sealed class FakeServer : IPathMethodHandler, IAsyncDisposable
    {
        private DBusConnection? _connection;

        public TaskCompletionSource<Notify> Notified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Path => DesktopNotifications.ObjectPath;

        public bool HandlesChildPaths => false;

        public static async Task<FakeServer> StartAsync()
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            var server = new FakeServer { _connection = connection };
            connection.AddMethodHandler(server);
            await connection.RequestNameAsync(DesktopNotifications.Service, RequestNameOptions.None);
            return server;
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if ((request.InterfaceAsString, request.MemberAsString) == (DesktopNotifications.Interface, "Notify"))
            {
                var reader = request.GetBodyReader();
                var app = reader.ReadString();
                reader.ReadUInt32();
                reader.ReadString();
                var summary = reader.ReadString();
                var body = reader.ReadString();
                var actions = reader.ReadArrayOfString().ToList();
                var hints = reader.ReadDictionaryOfStringToVariantValue();
                using (var writer = context.CreateReplyWriter("u"))
                {
                    writer.WriteUInt32(ServerId);
                    context.Reply(writer.CreateMessage());
                }

                Notified.TrySetResult(new Notify(app, summary, body, actions, hints.TryGetValue("desktop-entry", out var entry) ? entry.GetString() : null));
            }

            return default;
        }

        /// <summary>What a server sends when the user clicks: ActionInvoked(id, key).</summary>
        public void Invoke(uint id, string action)
        {
            using var writer = _connection!.GetMessageWriter();
            writer.WriteSignalHeader(path: DesktopNotifications.ObjectPath, @interface: DesktopNotifications.Interface, member: "ActionInvoked", signature: "us");
            writer.WriteUInt32(id);
            writer.WriteString(action);
            _connection.TrySendMessage(writer.CreateMessage());
        }

        /// <summary>NotificationClosed(id, reason 2: dismissed by the user).</summary>
        public void Close(uint id)
        {
            using var writer = _connection!.GetMessageWriter();
            writer.WriteSignalHeader(path: DesktopNotifications.ObjectPath, @interface: DesktopNotifications.Interface, member: "NotificationClosed", signature: "uu");
            writer.WriteUInt32(id);
            writer.WriteUInt32(2);
            _connection.TrySendMessage(writer.CreateMessage());
        }

        public ValueTask DisposeAsync()
        {
            _connection?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
