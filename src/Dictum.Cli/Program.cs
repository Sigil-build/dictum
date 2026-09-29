using System.CommandLine;
using System.Text;
using Dictum.Cli;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = utf8;
Console.InputEncoding = utf8;
ConsoleInterrupt.Install();

var configuration = new InvocationConfiguration
{
    // Ctrl+C is handled by ConsoleInterrupt so that 'record' can stop instead of abort.
    ProcessTerminationTimeout = null,
};

return await CliApp.BuildRoot().Parse(args).InvokeAsync(configuration, ConsoleInterrupt.Token);
