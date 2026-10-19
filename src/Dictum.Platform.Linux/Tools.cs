using System.Diagnostics;
using System.Text;

namespace Dictum.Platform.Linux;

/// <summary>Runs small helper programs such as wl-copy, xprop or systemctl, with a timeout.</summary>
internal static class Tools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    // A forked child keeps the pipes open, so the output gets a short grace period, not an unbounded wait.
    private static readonly TimeSpan OutputGrace = TimeSpan.FromMilliseconds(100);

    public sealed record Result(int ExitCode, string Output, string Error);

    public static bool Exists(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, name)));

    public static async Task<Result> RunAsync(string file, IReadOnlyList<string> args, string? input, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        // wl-copy and xclip fork a child that keeps serving the selection with inherited pipes, so read
        // output only until the direct child exits.
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: false);
            throw new TimeoutException($"{file} did not finish within {(int)limit.TotalMilliseconds} ms.");
        }

        await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(OutputGrace, CancellationToken.None)).ConfigureAwait(false);
        return new Result(process.ExitCode, Completed(output), Completed(error));
    }

    private static string Completed(Task<string> read) => read.IsCompletedSuccessfully ? read.Result : string.Empty;
}
