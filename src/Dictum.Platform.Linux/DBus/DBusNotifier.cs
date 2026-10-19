using Dictum.Core.Diagnostics;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.DBus;

/// <summary>Desktop notifications via org.freedesktop.Notifications; later ones replace the previous one.</summary>
public sealed class DBusNotifier(Portal portal, ILog log) : INotifier
{
    private const byte NormalUrgency = 1;
    private const byte CriticalUrgency = 2;
    private const int InfoExpireMs = 3000;
    private const int ErrorExpireMs = 8000;

    private uint _lastId;

    public async Task NotifyAsync(string summary, string body, NotificationKind kind, CancellationToken ct)
    {
        try
        {
            var connection = await portal.ConnectAsync(ct).ConfigureAwait(false);
            _lastId = await connection.CallMethodAsync(
                Build(connection, summary, body, kind, _lastId),
                static (message, _) => message.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException)
        {
            log.Debug($"Notification not shown: {ex.Message}");
        }
    }

    private static MessageBuffer Build(DBusConnection connection, string summary, string body, NotificationKind kind, uint replaces)
    {
        var error = kind == NotificationKind.Error;
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            "org.freedesktop.Notifications", "/org/freedesktop/Notifications", "org.freedesktop.Notifications", "Notify", "susssasa{sv}i");
        writer.WriteString(DesktopIdentity.Name);
        writer.WriteUInt32(replaces);
        writer.WriteString(error ? "dialog-error" : DesktopIdentity.Icon);
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(Array.Empty<string>());
        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["urgency"] = error ? CriticalUrgency : NormalUrgency,
            ["desktop-entry"] = DesktopIdentity.AppId,
        });
        writer.WriteInt32(error ? ErrorExpireMs : InfoExpireMs);
        return writer.CreateMessage();
    }
}
