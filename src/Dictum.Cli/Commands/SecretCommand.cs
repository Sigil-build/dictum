using System.CommandLine;
using System.Text;

namespace Dictum.Cli.Commands;

internal static class SecretCommand
{
    public static Command Create()
    {
        var command = new Command("secret", "Store API keys in the OS secret store, for \"apiKey\": \"secret:<name>\"");

        var setName = new Argument<string>("name") { Description = "Secret name, e.g. groq" };
        var set = new Command("set", "Store a secret; the value is read from stdin or typed without echo") { setName };
        set.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            var name = parse.GetValue(setName)!;
            var value = Console.IsInputRedirected
                ? (await Console.In.ReadToEndAsync(token).ConfigureAwait(false)).Trim()
                : ReadHidden(context, $"Value for '{name}': ");
            if (value.Length == 0)
            {
                context.Error.WriteLine("error: the value is empty.");
                return ExitCodes.Error;
            }

            await context.Platform.Secrets.SetAsync(name, value, token).ConfigureAwait(false);
            context.Error.WriteLine($"Stored. Use \"apiKey\": \"secret:{name}\" in the config.");
            return ExitCodes.Success;
        }));

        var deleteName = new Argument<string>("name") { Description = "Secret name" };
        var delete = new Command("delete", "Remove a stored secret") { deleteName };
        delete.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, async (context, token) =>
        {
            await context.Platform.Secrets.DeleteAsync(parse.GetValue(deleteName)!, token).ConfigureAwait(false);
            context.Error.WriteLine("Deleted.");
            return ExitCodes.Success;
        }));

        command.Add(set);
        command.Add(delete);
        return command;
    }

    private static string ReadHidden(CliContext context, string prompt)
    {
        context.Error.Write(prompt);
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        context.Error.WriteLine();
        return value.ToString().Trim();
    }
}
