using System.CommandLine;

namespace Dictum.Cli.Tests;

/// <summary>Takes over stdout and stderr until disposed; a <see cref="CliContext"/> made meanwhile writes here.</summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _error = Console.Error;
    private readonly bool _outputIsTerminal = CliContext.OutputIsTerminal;

    /// <param name="terminal">Whether the CLI takes its stdout for a terminal; left false, it takes it for a file or a program.</param>
    public ConsoleCapture(bool terminal = false)
    {
        Console.SetOut(Out);
        Console.SetError(Error);
        CliContext.OutputIsTerminal = terminal;
    }

    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
        CliContext.OutputIsTerminal = _outputIsTerminal;
    }

    /// <summary>Runs the CLI with <paramref name="args"/>, as <c>dictum</c> would, and returns its exit code.</summary>
    public Task<int> RunAsync(params string[] args) =>
        CliApp.BuildRoot().Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = Out, Error = Error, ProcessTerminationTimeout = null },
            TestContext.Current.CancellationToken);
}
