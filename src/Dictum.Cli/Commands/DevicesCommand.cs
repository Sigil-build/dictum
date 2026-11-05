using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Audio;
using Dictum.Core.Text;

namespace Dictum.Cli.Commands;

internal static class DevicesCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("devices", "List microphones") { json };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, (context, _) =>
        {
            var devices = context.Host().Audio.ListDevices();
            if (parse.GetValue(json))
            {
                var list = devices.Select(d => new DeviceJson(d.Id, d.Name, d.HostApi, d.MaxInputChannels, d.DefaultSampleRate, d.IsDefault)).ToList();
                context.Out.WriteLine(JsonSerializer.Serialize(list, CliJsonContext.Readable.ListDeviceJson));
                return Task.FromResult(ExitCodes.Success);
            }

            if (devices.Count == 0)
            {
                context.Error.WriteLine("No microphones found.");
                return Task.FromResult(ExitCodes.Error);
            }

            foreach (var d in devices)
            {
                context.Out.WriteLine(Line(d));
            }

            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return command;
    }

    /// <summary>One microphone in the listing; its names are the driver's own, spelt out.</summary>
    internal static string Line(AudioDevice d) =>
        $"{(d.IsDefault ? "*" : " ")} {d.Id,3}  {TerminalText.Show(d.Name)}  ({TerminalText.Show(d.HostApi)}, {d.DefaultSampleRate:F0} Hz, {d.MaxInputChannels} ch)";
}
