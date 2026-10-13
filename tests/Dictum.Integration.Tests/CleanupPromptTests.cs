using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Processing;
using Dictum.Core.Text;
using Dictum.Processing;

namespace Dictum.Integration.Tests;

/// <summary>
/// The built-in cleanup prompt against a real llama-server. Enabled with DICTUM_INTEGRATION=1,
/// DICTUM_TEST_LLAMA_SERVER=&lt;llama-server executable&gt; and DICTUM_TEST_LLM_MODEL=&lt;GGUF file&gt;.
/// The guards are off (preserveText: false), so the test sees what the model does with the prompt itself;
/// run it after every change to a built-in prompt.
/// </summary>
public class CleanupPromptTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("DICTUM_TEST_LLAMA_SERVER");
    private static readonly string? Model = Environment.GetEnvironmentVariable("DICTUM_TEST_LLM_MODEL");

    [Fact]
    public async Task CleanupPromptFixesWithoutRewriting()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DICTUM_INTEGRATION") == "1", "Set DICTUM_INTEGRATION=1 to run");
        Assert.SkipWhen(string.IsNullOrEmpty(Server) || !File.Exists(Server), "Set DICTUM_TEST_LLAMA_SERVER to a llama-server executable");
        Assert.SkipWhen(string.IsNullOrEmpty(Model) || !File.Exists(Model), "Set DICTUM_TEST_LLM_MODEL to a GGUF file");
        var ct = TestContext.Current.CancellationToken;

        using var server = await LlamaServer.StartAsync(Server!, Model!, ct);
        using var http = new HttpClient();
        var processor = new LlmProcessor(
            "cleanup",
            new LlmProcessorConfig
            {
                BaseUrl = server.BaseUrl,
                Model = "test",
                Prompt = "cleanup",
                ApiKey = LlamaServer.ApiKey,
                Temperature = 0,
                TimeoutMs = 120_000,
                OnError = LlmErrorMode.Fail,
                PreserveText = false,
            },
            http,
            new ConfigValueResolver(new UnavailableSecretStore()),
            new PromptTemplates(Path.Combine(Path.GetTempPath(), "dictum-no-user-prompts")),
            NullLog.Instance);

        var failures = new List<string>();
        foreach (var phrase in Phrases())
        {
            var answer = await processor.ProcessAsync(phrase.Text, new ProcessingContext("test", phrase.Language, null), ct);
            if (TextChecks.HasLineBreak(answer))
            {
                failures.Add($"{phrase.Id}: added line breaks: {answer}");
            }

            if (TextChecks.WordCount(answer) * 3 < TextChecks.WordCount(phrase.Text))
            {
                failures.Add($"{phrase.Id}: dropped most words: {answer}");
            }

            if (TextChecks.IsScriptFlip(phrase.Text, answer))
            {
                failures.Add($"{phrase.Id}: translated: {answer}");
            }

            if (phrase.Expect == "not-answered" && !string.Equals(FirstWord(answer), FirstWord(phrase.Text), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{phrase.Id}: answered the request: {answer}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string FirstWord(string text) =>
        new string(text.TrimStart().TakeWhile(char.IsLetter).ToArray());

    private static IEnumerable<Phrase> Phrases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "cleanup-phrases.json")));
        return [.. document.RootElement.EnumerateArray().Select(e => new Phrase(
            e.GetProperty("id").GetString()!,
            e.GetProperty("language").GetString()!,
            e.GetProperty("text").GetString()!,
            e.GetProperty("expect").GetString()!))];
    }

    private sealed record Phrase(string Id, string Language, string Text, string Expect);

    private sealed class LlamaServer : IDisposable
    {
        public const string ApiKey = "integration-test";

        private readonly Process _process;

        private LlamaServer(Process process, int port)
        {
            _process = process;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
        }

        public string BaseUrl { get; }

        public static async Task<LlamaServer> StartAsync(string executable, string model, CancellationToken ct)
        {
            var port = FreePort();
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
            {
                "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture), "--api-key", ApiKey, "-m", model,
                "-ngl", "99", "-c", "4096", "-np", "1", "--jinja", "--reasoning", "off", "--no-ui", "--no-slots",
            })
            {
                info.ArgumentList.Add(argument);
            }

            var process = Process.Start(info) ?? throw new InvalidOperationException("llama-server did not start");
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var server = new LlamaServer(process, port);

            using var http = new HttpClient();
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    server.Dispose();
                    throw new InvalidOperationException($"llama-server exited with code {process.ExitCode}");
                }

                try
                {
                    using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health"), ct);
                    if (response.IsSuccessStatusCode)
                    {
                        return server;
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(500, ct);
            }

            server.Dispose();
            throw new TimeoutException("llama-server was not ready within 3 minutes");
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }

            _process.Dispose();
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
