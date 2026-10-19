using Dictum.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.Tray;

/// <summary>Method-call plumbing shared by the objects the tray exports on the bus.</summary>
internal static class ExportedObject
{
    /// <summary>
    /// Runs <paramref name="handle"/> and answers an exception with an error reply: Tmds.DBus closes the whole
    /// connection when an exception escapes a method handler.
    /// </summary>
    public static ValueTask HandleSafely(MethodContext context, Action<MethodContext> handle)
    {
        try
        {
            handle(context);
        }
        catch (Exception ex)
        {
            context.ReplyError(DBusNames.FailedError, ex.Message);
        }

        return default;
    }

    public static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    public static void ReplyVariant(MethodContext context, VariantValue value)
    {
        using var writer = context.CreateReplyWriter("v");
        writer.WriteVariant(value);
        context.Reply(writer.CreateMessage());
    }
}
