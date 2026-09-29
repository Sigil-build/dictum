using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;

namespace Dictum.Cli.Commands;

internal static class DevicesCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Print JSON" };
        var command = new Command("devices", "List microphones") { json };
        command.SetAction((parse, ct) => CliApp.RunAsync(parse, ct, (context, _) =>
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
                context.Out.WriteLine($"{(d.IsDefault ? "*" : " ")} {d.Id,3}  {d.Name}  ({d.HostApi}, {d.DefaultSampleRate:F0} Hz, {d.MaxInputChannels} ch)");
            }

            return Task.FromResult(ExitCodes.Success);
        }));
        return command;
    }
}
