using Tmds.DBus.Protocol;

namespace Dictum.Platform.Linux.Tray;

/// <summary>How a <see cref="MenuNode"/> goes on the wire: its dbusmenu properties and its (ia{sv}av) layout.</summary>
internal static class MenuNodeWriter
{
    public const string LayoutSignature = "(ia{sv}av)";

    private static class NodeProperty
    {
        public const string Type = "type";
        public const string Label = "label";
        public const string Enabled = "enabled";
        public const string ToggleType = "toggle-type";
        public const string ToggleState = "toggle-state";
        public const string ChildrenDisplay = "children-display";
    }

    private static readonly string[] AllProperties =
    [
        NodeProperty.Type, NodeProperty.Label, NodeProperty.Enabled, NodeProperty.ToggleType, NodeProperty.ToggleState,
        NodeProperty.ChildrenDisplay,
    ];

    /// <summary>Writes the struct (ia{sv}av). Children are variants that each hold another (ia{sv}av).</summary>
    public static void WriteLayout(ref MessageWriter writer, MenuNode node, int depth, string[] names)
    {
        writer.WriteStructureStart();
        writer.WriteInt32(node.Id);
        WriteProperties(ref writer, node, names);
        var children = writer.WriteArrayStart(DBusType.Variant);
        if (depth != 0)
        {
            foreach (var child in node.Children ?? [])
            {
                writer.WriteSignature(LayoutSignature); // variant header
                WriteLayout(ref writer, child, depth < 0 ? -1 : depth - 1, names);
            }
        }

        writer.WriteArrayEnd(children);
    }

    /// <summary>a{sv}; an empty <paramref name="names"/> means all. Only non-default values are sent, as dbusmenu expects.</summary>
    public static void WriteProperties(ref MessageWriter writer, MenuNode node, string[] names)
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

    /// <summary>The property's value, or null when it has its default value or does not exist.</summary>
    public static VariantValue? Property(MenuNode node, string name) => name switch
    {
        NodeProperty.Type when node.Separator => VariantValue.String("separator"),
        NodeProperty.Label when !node.Separator && node.Id != 0 => VariantValue.String(node.Label), // "_" = mnemonic, "__" = literal "_"
        NodeProperty.Enabled when !node.Enabled => VariantValue.Bool(false),
        NodeProperty.ToggleType when node.ToggleType.Length > 0 => VariantValue.String(node.ToggleType),
        NodeProperty.ToggleState when node.ToggleType.Length > 0 => VariantValue.Int32(node.ToggleState),
        NodeProperty.ChildrenDisplay when node.Children is { Count: > 0 } => VariantValue.String("submenu"),
        _ => (VariantValue?)null,
    };
}
