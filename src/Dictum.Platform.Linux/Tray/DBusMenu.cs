using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.Tray;

/// <summary>One dbusmenu node. Id 0 is the root. Ids must be stable; Plasma never re-reads type,
/// toggle-type or children-display for an id it already knows, so changing those needs a new id.</summary>
public sealed record MenuNode(
    int Id,
    string Label = "",
    bool Enabled = true,
    bool Visible = true,
    bool Separator = false,
    string ToggleType = "",   // "", "checkmark", "radio"
    int ToggleState = -1,     // 0 off, 1 on, -1 indeterminate
    string IconName = "",
    string[][]? Shortcut = null, // [["Control","Alt","space"]] - shown right-aligned by Qt
    IReadOnlyList<MenuNode>? Children = null);

/// <summary>com.canonical.dbusmenu exported at /MenuBar, hand-written over Tmds.DBus.Protocol.</summary>
public sealed class DBusMenu(DBusConnection connection) : IPathMethodHandler
{
    public const string Interface = "com.canonical.dbusmenu";
    public const string MenuPath = "/MenuBar";

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
        var revision = Volatile.Read(ref _layout).Revision + 1; // single writer (UI thread)
        Volatile.Write(ref _layout, new Snapshot(revision, root, byId));

        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(path: MenuPath, @interface: Interface, member: "LayoutUpdated", signature: "ui");
            writer.WriteUInt32(revision);
            writer.WriteInt32(0);
            connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }

        static void Index(MenuNode node, Dictionary<int, MenuNode> byId)
        {
            byId[node.Id] = node;
            foreach (var child in node.Children ?? [])
            {
                Index(child, byId);
            }
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            Handle(context);
        }
        catch (Exception ex)
        {
            context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
        }

        return default;
    }

    private void Handle(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([InterfaceXml, IntrospectionXml.DBusProperties]);
            return;
        }

        var request = context.Request;
        var reader = request.GetBodyReader();
        var layout = Volatile.Read(ref _layout);

        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context);
            return;
        }

        if (request.InterfaceAsString != Interface)
        {
            return;
        }

        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case ("GetLayout", "iias"):
            {
                var parentId = reader.ReadInt32();
                var depth = reader.ReadInt32();
                var names = reader.ReadArrayOfString();
                if (!layout.ById.TryGetValue(parentId, out var parent))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"Unknown menu id {parentId}");
                    return;
                }

                var writer = context.CreateReplyWriter("u(ia{sv}av)");
                try
                {
                    writer.WriteUInt32(layout.Revision);
                    WriteLayout(ref writer, parent, depth, names);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case ("GetGroupProperties", "aias"):
            {
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
                        WriteProperties(ref writer, node, names);
                    }

                    writer.WriteArrayEnd(array);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case ("GetProperty", "is"):
            {
                var id = reader.ReadInt32();
                var name = reader.ReadString();
                var value = layout.ById.TryGetValue(id, out var node) ? Property(node, name) : null;
                if (value is null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"No property {name} on {id}");
                    return;
                }

                var writer = context.CreateReplyWriter("v");
                try
                {
                    writer.WriteVariant(value.Value);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case ("Event", "isvu"):
            {
                var id = reader.ReadInt32();
                var eventId = reader.ReadString();
                reader.ReadVariantValue(); // data: Plasma sends s "", GNOME sends i 0
                reader.ReadUInt32();       // timestamp
                if (eventId == "clicked")
                {
                    RaiseClicked(id);
                }

                ReplyEmpty(context);
                break;
            }

            case ("EventGroup", "a(isvu)"):
            {
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
                    else if (eventId == "clicked")
                    {
                        RaiseClicked(id);
                    }
                }

                var writer = context.CreateReplyWriter("ai");
                try
                {
                    writer.WriteArray(errors.ToArray());
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case ("AboutToShow", "i"):
            {
                reader.ReadInt32();
                var writer = context.CreateReplyWriter("b");
                try
                {
                    writer.WriteBool(false); // we push LayoutUpdated ourselves
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case ("AboutToShowGroup", "ai"):
            {
                var ids = reader.ReadArrayOfInt32();
                var writer = context.CreateReplyWriter("aiai");
                try
                {
                    writer.WriteArray(Array.Empty<int>());
                    writer.WriteArray(ids.Where(id => !layout.ById.ContainsKey(id)).ToArray());
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }
        }
    }

    private void RaiseClicked(int id)
    {
        if (Clicked is { } clicked)
        {
            ThreadPool.QueueUserWorkItem(_ => clicked(id));
        }
    }

    /// <summary>Writes the struct (ia{sv}av). Children are variants that each hold another (ia{sv}av).</summary>
    private static void WriteLayout(ref MessageWriter writer, MenuNode node, int depth, string[] names)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(node.Id);
        WriteProperties(ref writer, node, names);
        var children = writer.WriteArrayStart(DBusType.Variant);
        if (depth != 0)
        {
            foreach (var child in node.Children ?? [])
            {
                writer.WriteSignature("(ia{sv}av)"); // variant header
                WriteLayout(ref writer, child, depth < 0 ? -1 : depth - 1, names);
            }
        }

        writer.WriteArrayEnd(children);
    }

    /// <summary>a{sv}; an empty <paramref name="names"/> means all. Only non-default values are sent (spec).</summary>
    private static void WriteProperties(ref MessageWriter writer, MenuNode node, string[] names)
    {
        var dict = writer.WriteDictionaryStart();
        foreach (var name in AllProperties)
        {
            if (names.Length > 0 && Array.IndexOf(names, name) < 0)
            {
                continue;
            }

            if (Property(node, name) is { } value)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(name);
                writer.WriteVariant(value);
            }
        }

        writer.WriteDictionaryEnd(dict);
    }

    private static readonly string[] AllProperties =
        ["type", "label", "enabled", "visible", "icon-name", "toggle-type", "toggle-state", "children-display", "shortcut"];

    private static VariantValue? Property(MenuNode node, string name) => name switch
    {
        "type" when node.Separator => VariantValue.String("separator"),
        "label" when !node.Separator && node.Id != 0 => VariantValue.String(node.Label), // "_" = mnemonic, "__" = literal "_"
        "enabled" when !node.Enabled => VariantValue.Bool(false),
        "visible" when !node.Visible => VariantValue.Bool(false),
        "icon-name" when node.IconName.Length > 0 => VariantValue.String(node.IconName),
        "toggle-type" when node.ToggleType.Length > 0 => VariantValue.String(node.ToggleType),
        "toggle-state" when node.ToggleType.Length > 0 => VariantValue.Int32(node.ToggleState),
        "children-display" when node.Children is { Count: > 0 } => VariantValue.String("submenu"),
        "shortcut" when node.Shortcut is { } keys => Shortcut(keys),
        _ => (VariantValue?)null,
    };

    /// <summary>aas: [["Control","Alt","space"]].</summary>
    private static VariantValue Shortcut(string[][] keys)
    {
        var outer = new Array<Array<string>>();
        foreach (var chord in keys)
        {
            var inner = new Array<string>();
            foreach (var key in chord)
            {
                inner.Add(key);
            }

            outer.Add(inner);
        }

        return outer.AsVariantValue();
    }

    private void HandleProperties(MethodContext context)
    {
        var request = context.Request;
        var reader = request.GetBodyReader();
        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case ("GetAll", "s") when reader.ReadString() == Interface:
            {
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = writer.WriteDictionaryStart();
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("Version");
                    writer.WriteVariantUInt32(3);
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("TextDirection");
                    writer.WriteVariantString("ltr");
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("Status");
                    writer.WriteVariantString("normal");
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString("IconThemePath");
                    writer.WriteSignature("as");
                    writer.WriteArray(Array.Empty<string>());
                    writer.WriteDictionaryEnd(dict);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case ("Get", "ss") when reader.ReadString() == Interface:
            {
                var name = reader.ReadString();
                VariantValue? value = name switch
                {
                    "Version" => VariantValue.UInt32(3),
                    "TextDirection" => VariantValue.String("ltr"),
                    "Status" => VariantValue.String("normal"),
                    "IconThemePath" => VariantValue.Array(Array.Empty<string>()),
                    _ => (VariantValue?)null,
                };
                if (value is null)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                    return;
                }

                var writer = context.CreateReplyWriter("v");
                try
                {
                    writer.WriteVariant(value.Value);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", "No such interface");
                break;
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }
}
