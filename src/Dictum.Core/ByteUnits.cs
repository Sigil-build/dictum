namespace Dictum.Core;

/// <summary>
/// Byte multiples for sizes shown to people: file sizes in decimal megabytes and gigabytes, as file managers show them;
/// memory in binary gibibytes, as Windows Task Manager counts it.
/// </summary>
public static class ByteUnits
{
    public const long Megabyte = 1_000_000;
    public const long Gigabyte = 1_000_000_000;
    public const long Gibibyte = 1L << 30;
}
