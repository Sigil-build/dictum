using System.Text;
using Dictum.Core.Configuration;

namespace Dictum.Cli;

/// <summary>Questions on stderr, answers from the console; stdout stays free for results.</summary>
internal sealed class ConsolePrompt(TextWriter output)
{
    public string Ask(string question, string? defaultValue)
    {
        output.Write(defaultValue is { Length: > 0 } ? $"{question} [{defaultValue}]: " : $"{question}: ");
        var line = ReadLine().Trim();
        return line.Length == 0 ? defaultValue ?? string.Empty : line;
    }

    public bool Confirm(string question, bool defaultValue)
    {
        while (true)
        {
            output.Write($"{question} [{(defaultValue ? "Y/n" : "y/N")}]: ");
            var answer = ReadLine().Trim().ToLowerInvariant();
            if (answer.Length == 0)
            {
                return defaultValue;
            }

            if (answer is "y" or "yes" or "т" or "так")
            {
                return true;
            }

            if (answer is "n" or "no" or "н" or "ні")
            {
                return false;
            }

            output.WriteLine("Please answer y or n.");
        }
    }

    public int Choose(string question, IReadOnlyList<string> options, int defaultIndex)
    {
        output.WriteLine(question);
        for (var i = 0; i < options.Count; i++)
        {
            output.WriteLine($"  {i + 1}. {options[i]}");
        }

        while (true)
        {
            output.Write($"Choose 1-{options.Count} [{defaultIndex + 1}]: ");
            var answer = ReadLine().Trim();
            if (answer.Length == 0)
            {
                return defaultIndex;
            }

            if (int.TryParse(answer, out var number) && number >= 1 && number <= options.Count)
            {
                return number - 1;
            }

            output.WriteLine($"Please enter a number from 1 to {options.Count}.");
        }
    }

    /// <summary>Reads a secret without echoing it.</summary>
    public string ReadHidden(string prompt)
    {
        output.Write(prompt);
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

        output.WriteLine();
        return value.ToString().Trim();
    }

    private static string ReadLine() =>
        Console.ReadLine() ?? throw new ConfigException("Input ended before every question was answered.");
}
