using Dictum.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.Tray;

/// <summary>Immutable tray state; swapped atomically from the UI thread, read on the D-Bus receive loop.</summary>
public sealed record SniState(
    string Status,            // "Passive" | "Active" | "NeedsAttention"
    string IconName,          // e.g. "dictum-idle" (the loose file dictum-idle.svg in IconThemePath)
    string AttentionIconName, // shown while Status is NeedsAttention
    string IconThemePath,     // absolute dir, e.g. ~/.local/share/dictum/icons
    string Title,
    string ToolTipTitle,
    string ToolTipText);

/// <summary>org.kde.StatusNotifierItem exported at /StatusNotifierItem, hand-written over Tmds.DBus.Protocol.</summary>
public sealed class StatusNotifierItem(DBusConnection connection, string menuPath) : IPathMethodHandler
{
    public const string Interface = "org.kde.StatusNotifierItem";
    public const string ItemPath = "/StatusNotifierItem";

    private const string PixmapsSignature = "a(iiay)";
    private const string IdleIcon = "dictum-idle";

    private static class ItemProperty
    {
        public const string Category = "Category";
        public const string Id = "Id";
        public const string Title = "Title";
        public const string Status = "Status";
        public const string WindowId = "WindowId";
        public const string IconThemePath = "IconThemePath";
        public const string Menu = "Menu";
        public const string ItemIsMenu = "ItemIsMenu";
        public const string IconName = "IconName";
        public const string IconPixmap = "IconPixmap";
        public const string OverlayIconName = "OverlayIconName";
        public const string OverlayIconPixmap = "OverlayIconPixmap";
        public const string AttentionIconName = "AttentionIconName";
        public const string AttentionIconPixmap = "AttentionIconPixmap";
        public const string AttentionMovieName = "AttentionMovieName";
        public const string ToolTip = "ToolTip";
    }

    private static readonly ReadOnlyMemory<byte> InterfaceXml = """
        <interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconThemePath" type="s" access="read"/>
          <property name="Menu" type="o" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/>
          <property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/>
          <property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <method name="ProvideXdgActivationToken"><arg name="token" type="s" direction="in"/></method>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewTitle"/>
          <signal name="NewIcon"/>
          <signal name="NewAttentionIcon"/>
          <signal name="NewOverlayIcon"/>
          <signal name="NewMenu"/>
          <signal name="NewToolTip"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface>

        """u8.ToArray();

    private static readonly string[] PropertyNames =
    [
        ItemProperty.Category, ItemProperty.Id, ItemProperty.Title, ItemProperty.Status, ItemProperty.WindowId,
        ItemProperty.IconThemePath, ItemProperty.Menu, ItemProperty.ItemIsMenu, ItemProperty.IconName, ItemProperty.IconPixmap,
        ItemProperty.OverlayIconName, ItemProperty.OverlayIconPixmap, ItemProperty.AttentionIconName,
        ItemProperty.AttentionIconPixmap, ItemProperty.AttentionMovieName, ItemProperty.ToolTip,
    ];

    private SniState _state = new("Active", IdleIcon, IdleIcon, "", DesktopIdentity.Name, DesktopIdentity.Name, "");

    /// <summary>Raised on a thread-pool thread, never on the D-Bus receive loop.</summary>
    public event Action<int, int>? Activated;
    public event Action<int, int>? SecondaryActivated;
    public event Action<string>? ActivationTokenProvided;

    public string Path => ItemPath;
    public bool HandlesChildPaths => false;

    public SniState State => Volatile.Read(ref _state);

    /// <summary>Swap the state and emit the New* signals hosts listen to (Plasma ignores PropertiesChanged on SNI).</summary>
    public void Update(SniState next)
    {
        var previous = Interlocked.Exchange(ref _state, next);

        // Plasma reloads icons only on the New* signals, through GetAll; NewStatus only copies the status, so the
        // icons must be known before the status changes.
        if (previous.Title != next.Title)
        {
            EmitSignal("NewTitle");
        }

        if (previous.IconName != next.IconName || previous.IconThemePath != next.IconThemePath)
        {
            EmitSignal("NewIcon");
        }

        if (previous.AttentionIconName != next.AttentionIconName)
        {
            EmitSignal("NewAttentionIcon");
        }

        if (previous.ToolTipTitle != next.ToolTipTitle || previous.ToolTipText != next.ToolTipText)
        {
            EmitSignal("NewToolTip");
        }

        if (previous.Status != next.Status)
        {
            EmitSignal("NewStatus", next.Status);
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

        var request = context.Request;
        if (context.IsPropertiesInterfaceRequest)
        {
            HandleProperties(context);
            return;
        }

        if (request.InterfaceAsString != Interface)
        {
            return; // MethodContext.Dispose() then replies UnknownMethod.
        }

        var reader = request.GetBodyReader();
        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case ("Activate", "ii"):
                Raise(Activated, reader.ReadInt32(), reader.ReadInt32());
                ExportedObject.ReplyEmpty(context);
                break;
            case ("SecondaryActivate", "ii"):
                Raise(SecondaryActivated, reader.ReadInt32(), reader.ReadInt32());
                ExportedObject.ReplyEmpty(context);
                break;
            case ("ContextMenu", "ii"):
                // Only called by hosts that could not use Menu (dbusmenu); nothing to do.
                ExportedObject.ReplyEmpty(context);
                break;
            case ("Scroll", "is"):
                ExportedObject.ReplyEmpty(context);
                break;
            case ("ProvideXdgActivationToken", "s"):
                var token = reader.ReadString();
                if (ActivationTokenProvided is { } provided)
                {
                    ThreadPool.QueueUserWorkItem(_ => provided(token));
                }

                ExportedObject.ReplyEmpty(context);
                break;
        }
    }

    private void HandleProperties(MethodContext context)
    {
        var request = context.Request;
        var reader = request.GetBodyReader();
        switch ((request.MemberAsString, request.SignatureAsString))
        {
            case (DBusNames.Get, "ss"):
            {
                var iface = reader.ReadString();
                var name = reader.ReadString();
                if (iface != Interface || Array.IndexOf(PropertyNames, name) < 0)
                {
                    context.ReplyError(DBusNames.UnknownPropertyError, $"No property {iface}.{name}");
                    return;
                }

                var state = State;
                var writer = context.CreateReplyWriter("v");
                try
                {
                    WritePropertyValue(ref writer, name, state);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case (DBusNames.GetAll, "s"):
            {
                if (reader.ReadString() != Interface)
                {
                    context.ReplyError(DBusNames.UnknownInterfaceError, "No such interface");
                    return;
                }

                var state = State;
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    var dict = writer.WriteDictionaryStart();
                    foreach (var name in PropertyNames)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString(name);
                        WritePropertyValue(ref writer, name, state);
                    }

                    writer.WriteDictionaryEnd(dict);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }

                break;
            }

            case (DBusNames.Set, "ssv"):
                context.ReplyError(DBusNames.PropertyReadOnlyError, "Properties are read-only");
                break;
        }
    }

    /// <summary>Writes one property as a variant: signature, then value.</summary>
    private void WritePropertyValue(ref MessageWriter writer, string name, SniState state)
    {
        switch (name)
        {
            case ItemProperty.Category: writer.WriteVariantString("ApplicationStatus"); break;
            case ItemProperty.Id: writer.WriteVariantString(PlasmaTray.ItemId); break;
            case ItemProperty.Title: writer.WriteVariantString(state.Title); break;
            case ItemProperty.Status: writer.WriteVariantString(state.Status); break;
            case ItemProperty.WindowId: writer.WriteVariantInt32(0); break;
            case ItemProperty.IconThemePath: writer.WriteVariantString(state.IconThemePath); break;
            case ItemProperty.Menu: writer.WriteVariantObjectPath(menuPath); break;
            case ItemProperty.ItemIsMenu: writer.WriteVariantBool(false); break;
            case ItemProperty.IconName: writer.WriteVariantString(state.IconName); break;
            case ItemProperty.OverlayIconName: writer.WriteVariantString(""); break;
            case ItemProperty.AttentionIconName: writer.WriteVariantString(state.AttentionIconName); break;
            case ItemProperty.AttentionMovieName: writer.WriteVariantString(""); break;
            case ItemProperty.IconPixmap or ItemProperty.OverlayIconPixmap or ItemProperty.AttentionIconPixmap:
                writer.WriteSignature(PixmapsSignature);
                WritePixmaps(ref writer, []); // none: hosts load IconName from IconThemePath
                break;
            case ItemProperty.ToolTip:
                writer.WriteSignature("(s" + PixmapsSignature + "ss)");
                writer.WriteStructureStart();
                writer.WriteString("");                // icon name
                WritePixmaps(ref writer, []);          // icon pixmaps
                writer.WriteString(state.ToolTipTitle);
                writer.WriteString(state.ToolTipText); // subset of HTML allowed
                break;
        }
    }

    /// <summary>a(iiay): each entry is (width, height, ARGB32 bytes in network byte order).</summary>
    private static void WritePixmaps(ref MessageWriter writer, ReadOnlySpan<(int Width, int Height, byte[] Argb)> pixmaps)
    {
        var array = writer.WriteArrayStart(DBusType.Struct);
        foreach (var (width, height, argb) in pixmaps)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(width);
            writer.WriteInt32(height);
            writer.WriteArray(argb);
        }

        writer.WriteArrayEnd(array);
    }

    /// <summary>Handlers run on the connection's receive loop, which reads nothing else until they return.</summary>
    private static void Raise(Action<int, int>? handler, int x, int y)
    {
        if (handler is not null)
        {
            ThreadPool.QueueUserWorkItem(_ => handler(x, y));
        }
    }

    private void EmitSignal(string member, string? status = null)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(path: ItemPath, @interface: Interface, member: member, signature: status is null ? null : "s");
        if (status is not null)
        {
            writer.WriteString(status);
        }

        connection.TrySendMessage(writer.CreateMessage()); // false when disconnected; the buffer is returned to the pool
    }
}
