namespace Dictum.Platform.Linux.DBus;

/// <summary>Names the D-Bus specification defines: the Properties interface and the standard errors.</summary>
internal static class DBusNames
{
    public const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    public const string Get = "Get";
    public const string GetAll = "GetAll";
    public const string Set = "Set";

    public const string FailedError = "org.freedesktop.DBus.Error.Failed";
    public const string InvalidArgsError = "org.freedesktop.DBus.Error.InvalidArgs";
    public const string PropertyReadOnlyError = "org.freedesktop.DBus.Error.PropertyReadOnly";
    public const string ServiceUnknownError = "org.freedesktop.DBus.Error.ServiceUnknown";
    public const string UnknownInterfaceError = "org.freedesktop.DBus.Error.UnknownInterface";
    public const string UnknownMethodError = "org.freedesktop.DBus.Error.UnknownMethod";
    public const string UnknownObjectError = "org.freedesktop.DBus.Error.UnknownObject";
    public const string UnknownPropertyError = "org.freedesktop.DBus.Error.UnknownProperty";
}
