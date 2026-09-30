using System.Diagnostics;
using System.Text;

namespace Dictum.Platform.Linux;

/// <summary>Runs small helper programs (wl-copy, xclip, xprop, wtype, systemctl) with a timeout.</summary>
internal static class Tools
{
    public sealed record Result(int ExitCode, string Output, string Error);

    public static bool Exists(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, name)));

    public static async Task<Result> RunAsync(string file, IReadOnlyList<string> args, string? input, CancellationToken ct, int timeoutMs = 3000)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        if (input is not null)
        {
            start.StandardInputEncoding = new UTF8Encoding(false);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        // wl-copy and xclip fork a child that keeps serving the selection with inherited pipes, so read
        // output only until the direct child exits.
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: false);
            throw new TimeoutException($"{file} did not finish within {timeoutMs} ms.");
        }

        // A forked child keeps the pipes open, so give the output a short grace period, not an unbounded wait.
        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(100, CancellationToken.None)).ConfigureAwait(false);
        return new Result(process.ExitCode, Completed(output), Completed(error));
    }

    private static string Completed(Task<string> read) => read.IsCompletedSuccessfully ? read.Result : string.Empty;
}
