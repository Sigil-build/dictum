using System.CommandLine;
using System.CommandLine.Completions;
using System.Globalization;
using System.Text;

namespace Dictum.Cli.Tests.Docs;

/// <summary>Renders the command tree as the markdown of docs/reference/cli.md.</summary>
internal static class CliReference
{
    public static string Render(RootCommand root)
    {
        var page = new StringBuilder();
        page.Append("# Command line\n\n");
        page.Append("<!-- Generated from the program by CliReferenceTests. Do not edit: run DICTUM_WRITE_DOCS=1 dotnet test --project tests/Dictum.Cli.Tests --filter-class \"*.CliReferenceTests\" -->\n\n");
        page.Append(Escape(root.Description)).Append("\n\n");
        page.Append("Every command takes `--help` (`-h`) to print its own help, and `dictum --version` prints the version.\n\n");
        page.Append("## Global options {#global-options}\n\n");
        page.Append("These options work on every command, before or after the command's name. The commands below list only their own options.\n\n");
        OptionTable(page, root.Options.Where(o => o.Recursive));

        foreach (var command in root.Subcommands)
        {
            Section(page, command, [command.Name]);
        }

        return page.ToString().TrimEnd('\n') + "\n";
    }

    private static void Section(StringBuilder page, Command command, List<string> path)
    {
        if (command.Hidden)
        {
            return;
        }

        var name = $"dictum {string.Join(' ', path)}";
        var options = Visible(command.Options).ToList();
        var arguments = command.Arguments.Where(a => !a.Hidden).ToList();
        var children = command.Subcommands.Where(c => !c.Hidden).ToList();

        page.Append(CultureInfo.InvariantCulture, $"## `{name}` {{#{string.Join('-', path)}}}\n\n");
        page.Append(Escape(command.Description)).Append("\n\n");

        var usage = new StringBuilder(name);
        if (children.Count > 0)
        {
            usage.Append(" <command>");
        }

        foreach (var argument in arguments)
        {
            usage.Append(' ').Append(Usage(argument));
        }

        if (options.Count > 0)
        {
            usage.Append(" [options]");
        }

        page.Append(CultureInfo.InvariantCulture, $"Usage: `{usage}`\n\n");

        if (arguments.Count > 0)
        {
            page.Append("| Argument | Description |\n| --- | --- |\n");
            foreach (var argument in arguments)
            {
                var cell = Escape(argument.Description);
                if (argument.HasDefaultValue)
                {
                    cell += $"<br>Default: {DefaultText(argument.GetDefaultValue())}";
                }

                page.Append(CultureInfo.InvariantCulture, $"| `<{argument.Name}>` | {cell} |\n");
            }

            page.Append('\n');
        }

        if (options.Count > 0)
        {
            OptionTable(page, options);
            var recursive = options.Where(o => o.Recursive).Select(o => $"`{o.Name}`").ToList();
            if (recursive.Count > 0 && children.Count > 0)
            {
                page.Append(CultureInfo.InvariantCulture, $"{string.Join(", ", recursive)} also works on every subcommand of `{name}`.\n\n");
            }
        }

        if (children.Count > 0)
        {
            page.Append("| Command | Description |\n| --- | --- |\n");
            foreach (var child in children)
            {
                page.Append(CultureInfo.InvariantCulture, $"| [`{child.Name}`](#{string.Join('-', path.Append(child.Name))}) | {Escape(child.Description)} |\n");
            }

            page.Append('\n');
        }

        foreach (var child in children)
        {
            Section(page, child, [.. path, child.Name]);
        }
    }

    private static IEnumerable<Option> Visible(IEnumerable<Option> options) =>
        options.Where(o => !o.Hidden && o.Name is not ("--help" or "--version"));

    private static void OptionTable(StringBuilder page, IEnumerable<Option> options)
    {
        page.Append("| Option | Description |\n| --- | --- |\n");
        foreach (var option in Visible(options))
        {
            var names = string.Join(", ", new[] { option.Name }.Concat(option.Aliases.Order(StringComparer.Ordinal)).Select(n => $"`{n}`"));
            var cell = Escape(option.Description);
            if (option.HasDefaultValue && option.ValueType != typeof(bool))
            {
                cell += $"<br>Default: {DefaultText(option.GetDefaultValue())}";
            }

            if (option.ValueType != typeof(bool))
            {
                var values = option.GetCompletions(CompletionContext.Empty).Select(c => c.Label).ToList();
                if (values.Count > 0)
                {
                    cell += $"<br>Values: {string.Join(", ", values)}";
                }
            }

            page.Append(CultureInfo.InvariantCulture, $"| {names} | {cell} |\n");
        }

        page.Append('\n');
    }

    private static string DefaultText(object? value) => Escape(Convert.ToString(value, CultureInfo.InvariantCulture));

    private static string Usage(Argument argument) => argument.Arity switch
    {
        { MinimumNumberOfValues: 0, MaximumNumberOfValues: 1 } => $"[<{argument.Name}>]",
        { MaximumNumberOfValues: > 1 } => argument.Arity.MinimumNumberOfValues == 0 ? $"[<{argument.Name}>...]" : $"<{argument.Name}>...",
        _ => $"<{argument.Name}>",
    };

    private static string Escape(string? text) =>
        (text ?? "").Replace("|", "\\|", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
