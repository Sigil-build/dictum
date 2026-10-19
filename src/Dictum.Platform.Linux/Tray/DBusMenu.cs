using Dictum.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.Tray;

/// <summary>com.canonical.dbusmenu exported at /MenuBar, hand-written over Tmds.DBus.Protocol.</summary>
public sealed class DBusMenu(DBusConnection connection) : IPathMethodHandler
{
    public const string Interface = "com.canonical.dbusmenu";
    public const string MenuPath = "/MenuBar";

    private const string ClickedEvent = "clicked";
    private const uint ProtocolVersion = 3;
    private const string LeftToRight = "ltr";
    private const string NormalStatus = "normal";

    private static class MenuProperty
    {
        public const string Version = "Version";
        public const string TextDirection = "TextDirection";
        public const string Status = "Status";
        public const string IconThemePath = "IconThemePath";
    }

    private static readonly ReadOnlyMemory<byte> InterfaceXml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout">
            <arg type="i" name="parentId" direction="in"/>
            <arg type="i" name="recursionDepth" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="u" name="revision" direction="out"/>
            <arg type="(ia{sv}av)" name="layout" direction="out"/>
          </method>
          <method name="GetGroupProperties">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="as" name="propertyNames" direction="in"/>
            <arg type="a(ia{sv})" name="properties" direction="out"/>
          </method>
          <method name="GetProperty">
            <arg type="i" name="id" direction="in"/>
            <arg type="s" name="name" direction="in"/>
            <arg type="v" name="value" direction="out"/>
          </method>
          <method name="Event">
            <arg type="i" name="id" direction="in"/>
            <arg type="s" name="eventId" direction="in"/>
            <arg type="v" name="data" direction="in"/>
            <arg type="u" name="timestamp" direction="in"/>
          </method>
          <method name="EventGroup">
            <arg type="a(isvu)" name="events" direction="in"/>
            <arg type="ai" name="idErrors" direction="out"/>
          </method>
          <method name="AboutToShow">
            <arg type="i" name="id" direction="in"/>
            <arg type="b" name="needUpdate" direction="out"/>
          </method>
          <method name="AboutToShowGroup">
            <arg type="ai" name="ids" direction="in"/>
            <arg type="ai" name="updatesNeeded" direction="out"/>
            <arg type="ai" name="idErrors" direction="out"/>
          </method>
          <signal name="ItemsPropertiesUpdated">
            <arg type="a(ia{sv})" name="updatedProps"/>
            <arg type="a(ias)" name="removedProps"/>
          </signal>
          <signal name="LayoutUpdated">
            <arg type="u" name="revision"/>
            <arg type="i" name="parent"/>
          </signal>
          <signal name="ItemActivationRequested">
            <arg type="i" name="id"/>
            <arg type="u" name="timestamp"/>
          </signal>
        </interface>

        """u8.ToArray();

    private sealed record Snapshot(uint Revision, MenuNode Root, Dictionary<int, MenuNode> ById);

    private Snapshot _layout = new(1, new MenuNode(0), new() { [0] = new MenuNode(0) });

    /// <summary>Raised for "clicked" on a thread-pool thread, never on the D-Bus receive loop.</summary>
    public event Action<int>? Clicked;

    public string Path => MenuPath;
    public bool HandlesChildPaths => false;

    /// <summary>Replaces the whole menu and tells hosts to re-read it (LayoutUpdated(revision, 0)).</summary>
    public void SetMenu(MenuNode root)
    {
        var byId = new Dictionary<int, MenuNode>();
        Index(root, byId);
        var revision = Volatile.Read(ref _layout).Revision + 1; // only the UI thread writes, so this needs no lock
        Volatile.Write(ref _layout, new Snapshot(revision, root, byId));

        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(path: MenuPath, @interface: Interface, member: "LayoutUpdated", signature: "ui");
        writer.WriteUInt32(revision);
        writer.WriteInt32(0);
        connection.TrySendMessage(writer.CreateMessage());

        static void Index(MenuNode node, Dictionary<int, MenuNode> byId)
        {
            byId[node.Id] = node;
            foreach (var child in node.Children ?? [])
            {
                Index(child, byId);
            }
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context) => ExportedObject.HandleSafely(context, Handle);

    private void Handle(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([InterfaceXml, IntrospectionXml.DBusProperties]);
            return;
        }

        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context);
            return;
        }

        var request = context.Request;
        if (request.InterfaceAsString != Interface)
        {
            return;
        }

        var layout = Volatile.Read(ref _layout);
        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case ("GetLayout", "iias"):
                GetLayout(context, layout);
                break;
            case ("GetGroupProperties", "aias"):
                GetGroupProperties(context, layout);
                break;
            case ("GetProperty", "is"):
                GetProperty(context, layout);
                break;
            case ("Event", "isvu"):
                Event(context);
                break;
            case ("EventGroup", "a(isvu)"):
                EventGroup(context, layout);
                break;
            case ("AboutToShow", "i"):
                AboutToShow(context);
                break;
            case ("AboutToShowGroup", "ai"):
                AboutToShowGroup(context, layout);
                break;
        }
    }

    private static void GetLayout(MethodContext context, Snapshot layout)
    {
        var reader = context.Request.GetBodyReader();
        var parentId = reader.ReadInt32();
        var depth = reader.ReadInt32();
        var names = reader.ReadArrayOfString();
        if (!layout.ById.TryGetValue(parentId, out var parent))
        {
            context.ReplyError(DBusNames.InvalidArgsError, $"Unknown menu id {parentId}");
            return;
        }

        var writer = context.CreateReplyWriter("u" + MenuNodeWriter.LayoutSignature);
        try
        {
            writer.WriteUInt32(layout.Revision);
            MenuNodeWriter.WriteLayout(ref writer, parent, depth, names);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void GetGroupProperties(MethodContext context, Snapshot layout)
    {
        var reader = context.Request.GetBodyReader();
        var ids = reader.ReadArrayOfInt32();
        var names = reader.ReadArrayOfString();
        var writer = context.CreateReplyWriter("a(ia{sv})");
        try
        {
            var array = writer.WriteArrayStart(DBusType.Struct);
            // Empty ids = every item (GNOME's AppIndicator extension sends the ids it wants).
            foreach (var node in ids.Length == 0 ? layout.ById.Values : ids.Where(layout.ById.ContainsKey).Select(id => layout.ById[id]))
            {
                writer.WriteStructureStart();
                writer.WriteInt32(node.Id);
                MenuNodeWriter.WriteProperties(ref writer, node, names);
            }

            writer.WriteArrayEnd(array);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void GetProperty(MethodContext context, Snapshot layout)
    {
        var reader = context.Request.GetBodyReader();
        var id = reader.ReadInt32();
        var name = reader.ReadString();
        if (layout.ById.TryGetValue(id, out var node) && MenuNodeWriter.Property(node, name) is { } value)
        {
            ExportedObject.ReplyVariant(context, value);
        }
        else
        {
            context.ReplyError(DBusNames.InvalidArgsError, $"No property {name} on {id}");
        }
    }

    private void Event(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var id = reader.ReadInt32();
        var eventId = reader.ReadString();
        reader.ReadVariantValue(); // data: Plasma sends s "", GNOME sends i 0
        reader.ReadUInt32();       // timestamp
        if (eventId == ClickedEvent)
        {
            RaiseClicked(id);
        }

        ExportedObject.ReplyEmpty(context);
    }

    private void EventGroup(MethodContext context, Snapshot layout)
    {
        var reader = context.Request.GetBodyReader();
        var errors = new List<int>();
        var end = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var id = reader.ReadInt32();
            var eventId = reader.ReadString();
            reader.ReadVariantValue();
            reader.ReadUInt32();
            if (!layout.ById.ContainsKey(id))
            {
                errors.Add(id);
            }
            else if (eventId == ClickedEvent)
            {
                RaiseClicked(id);
            }
        }

        using var writer = context.CreateReplyWriter("ai");
        writer.WriteArray(errors.ToArray());
        context.Reply(writer.CreateMessage());
    }

    private static void AboutToShow(MethodContext context)
    {
        context.Request.GetBodyReader().ReadInt32();
        using var writer = context.CreateReplyWriter("b");
        writer.WriteBool(false); // we push LayoutUpdated ourselves
        context.Reply(writer.CreateMessage());
    }

    private static void AboutToShowGroup(MethodContext context, Snapshot layout)
    {
        var ids = context.Request.GetBodyReader().ReadArrayOfInt32();
        using var writer = context.CreateReplyWriter("aiai");
        writer.WriteArray(Array.Empty<int>());
        writer.WriteArray(ids.Where(id => !layout.ById.ContainsKey(id)).ToArray());
        context.Reply(writer.CreateMessage());
    }

    private void RaiseClicked(int id)
    {
        if (Clicked is { } clicked)
        {
            ThreadPool.QueueUserWorkItem(_ => clicked(id));
        }
    }

    private static void HandleProperties(MethodContext context)
    {
        var request = context.Request;
        var reader = request.GetBodyReader();
        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case (DBusNames.GetAll, "s") when reader.ReadString() == Interface:
            {
                using var writer = context.CreateReplyWriter("a{sv}");
                var dict = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString(MenuProperty.Version);
                writer.WriteVariantUInt32(ProtocolVersion);
                writer.WriteDictionaryEntryStart();
                writer.WriteString(MenuProperty.TextDirection);
                writer.WriteVariantString(LeftToRight);
                writer.WriteDictionaryEntryStart();
                writer.WriteString(MenuProperty.Status);
                writer.WriteVariantString(NormalStatus);
                writer.WriteDictionaryEntryStart();
                writer.WriteString(MenuProperty.IconThemePath);
                writer.WriteSignature("as");
                writer.WriteArray(Array.Empty<string>());
                writer.WriteDictionaryEnd(dict);
                context.Reply(writer.CreateMessage());
                break;
            }

            case (DBusNames.Get, "ss") when reader.ReadString() == Interface:
            {
                var name = reader.ReadString();
                VariantValue? value = name switch
                {
                    MenuProperty.Version => VariantValue.UInt32(ProtocolVersion),
                    MenuProperty.TextDirection => VariantValue.String(LeftToRight),
                    MenuProperty.Status => VariantValue.String(NormalStatus),
                    MenuProperty.IconThemePath => VariantValue.Array(Array.Empty<string>()),
                    _ => (VariantValue?)null,
                };
                if (value is null)
                {
                    context.ReplyError(DBusNames.UnknownPropertyError, name);
                    return;
                }

                ExportedObject.ReplyVariant(context, value.Value);
                break;
            }

            default:
                context.ReplyError(DBusNames.UnknownInterfaceError, "No such interface");
                break;
        }
    }
}
