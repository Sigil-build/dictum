using System.CommandLine;
using Dictum.Core.Configuration;

namespace Dictum.Cli.Commands;

internal static class SecretCommand
{
    public static Command Create()
    {
        var command = new Command("secret", "Store API keys in the OS secret store, for \"apiKey\": \"secret:<name>\"");

        var setName = new Argument<string>("name") { Description = "Secret name, e.g. groq" };
        var set = new Command("set", "Store a secret; the value is read from stdin or typed without echo") { setName };
        set.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var name = parse.GetValue(setName)!;
            var value = Console.IsInputRedirected
                ? (await Console.In.ReadToEndAsync(token).ConfigureAwait(false)).Trim()
                : new ConsolePrompt(context.Error).ReadHidden($"Value for '{name}': ");
            if (value.Length == 0)
            {
                context.Error.WriteLine("error: the value is empty.");
                return ExitCodes.Error;
            }

            await context.Platform.Secrets.SetAsync(name, value, token).ConfigureAwait(false);
            context.Error.WriteLine($"Stored. Use \"apiKey\": \"{ConfigValueResolver.SecretReference(name)}\" in the config.");
            return ExitCodes.Success;
        }, ct));

        var deleteName = new Argument<string>("name") { Description = "Secret name" };
        var delete = new Command("delete", "Remove a stored secret") { deleteName };
        delete.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            await context.Platform.Secrets.DeleteAsync(parse.GetValue(deleteName)!, token).ConfigureAwait(false);
            context.Error.WriteLine("Deleted.");
            return ExitCodes.Success;
        }, ct));

        var checkName = new Argument<string>("name") { Description = "Secret name" };
        var check = new Command("check", "Tell whether a secret is stored, without printing it") { checkName };
        check.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var name = parse.GetValue(checkName)!;
            var value = await context.Platform.Secrets.GetAsync(name, token).ConfigureAwait(false);
            if (value is null)
            {
                context.Error.WriteLine($"'{name}' is not stored.");
                return ExitCodes.Error;
            }

            context.Error.WriteLine($"'{name}' is stored ({value.Length} characters).");
            return ExitCodes.Success;
        }, ct));

        command.Add(set);
        command.Add(check);
        command.Add(delete);
        return command;
    }
}
