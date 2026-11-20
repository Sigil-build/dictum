using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Dictum.Updates.Install;

/// <summary>The Linux runners: one <c>/bin/sh -c</c> script, its output in the update's log.</summary>
internal static class ShellScript
{
    /// <summary>One word for sh, whatever it holds: in single quotes, each <c>'</c> as <c>'\''</c>.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// The script's head: everything it prints goes to <paramref name="log"/>, then it waits for
    /// <paramref name="waitForProcess"/> to exit.
    /// </summary>
    public static StringBuilder Begin(string log, int waitForProcess) => new StringBuilder()
        .Append(CultureInfo.InvariantCulture, $"exec > {Quote(log)} 2>&1\n")
        .Append(CultureInfo.InvariantCulture, $"while kill -0 {waitForProcess} 2>/dev/null; do sleep 0.2; done\n");

    /// <summary>The script's last line: it becomes the program it starts, with no output and no input.</summary>
    public static StringBuilder Restart(this StringBuilder script, string? program, string? argument)
    {
        if (program is not null)
        {
            script.Append(CultureInfo.InvariantCulture, $"exec {Quote(program)}{(argument is null ? "" : " " + argument)} > /dev/null 2>&1 < /dev/null\n");
        }

        return script;
    }

    public static ProcessStartInfo Start(string script)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }
}
