using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Core.Audio;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Http;
using Dictum.Core.Stt;
using Dictum.Core.Text;

namespace Dictum.Plugins;

/// <summary>
/// A speech engine that runs as a plugin, spoken to under protocol 1. It resolves its plugin and takes its lease on
/// the plugin's process on first use, so a provider no profile uses starts nothing, and a reload that keeps the
/// provider keeps the process. A plugin that cannot start says why with a hint; anything else that goes wrong is a
/// <see cref="ProviderUnavailableException"/>.
/// </summary>
public sealed class PluginStt(
    string id,
    PluginSttConfig config,
    PluginResolver resolver,
    PluginHost host,
    ConfigValueResolver values,
    ILog log) : ISpeechToText, IDisposable
{
    private const string TranscriptionsPath = "audio/transcriptions";

    /// <summary>A transcription may take this long, and twice the clip's length on top: a plugin may run on a slow CPU.</summary>
    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private PluginLaunch? _launch;
    private PluginLease? _lease;
    private bool _disposed;

    public string Id => id;

    /// <summary>How messages name this provider.</summary>
    private string Name => $"Plugin provider '{id}'";

    private bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <exception cref="PluginUnavailableException">The plugin is not added, its model is missing, or it could not start.</exception>
    /// <exception cref="ProviderUnavailableException">It did not answer, answered an error, or was stopped meanwhile.</exception>
    public async Task<Transcript> TranscribeAsync(AudioClip clip, SttRequest request, CancellationToken ct)
    {
        var (_, _, endpoint) = await ReadyAsync(ct).ConfigureAwait(false);
        var server = new OpenAiCompatibleServer(host.Http, values, Name, new Uri(endpoint.Url, "/v1").ToString(), endpoint.Key);
        var watch = Stopwatch.StartNew();
        ServerAnswer answer;
        try
        {
            // A plugin that died meanwhile refuses the connection or drops it, which the server reports as unavailable.
            answer = await server.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, server.Endpoint(TranscriptionsPath)) { Content = BuildForm(config.Model, clip, request) },
                (int)(TranscribeTimeout + (clip.Duration * 2)).TotalMilliseconds,
                ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            // The host's client, which ends with the host at shutdown.
            throw Stopped(ex);
        }

        if (!answer.IsSuccess)
        {
            throw new ProviderUnavailableException(ErrorAnswer(Name, answer));
        }

        PluginTranscription? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(answer.Body, PluginJsonContext.Default.PluginTranscription);
        }
        catch (JsonException ex)
        {
            throw new ProviderUnavailableException($"{Name} returned invalid JSON: {TerminalText.Show(OpenAiCompatible.Snippet(answer.Body))}", ex);
        }

        log.Debug($"STT '{id}' answered in {watch.ElapsedMilliseconds} ms");
        return new Transcript(
            parsed?.Text ?? string.Empty,
            parsed?.Language ?? Language(request),
            watch.Elapsed,
            $"plugin {config.Plugin}",
            endpoint.Info.Model?.Id ?? config.Model);
    }

    /// <summary>
    /// Starts the plugin if it is not running yet, which may take as long as its start timeout. A failure is an error
    /// status, so a warm-up goes on to the next provider.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">
    /// A reload let go of this provider: as with a disposed engine, the caller asks the provider that replaced it.
    /// </exception>
    public async Task<HealthStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            var (launch, instance, endpoint) = await ReadyAsync(ct).ConfigureAwait(false);
            return HealthStatus.Ok($"plugin {launch.Name} {launch.Version}, model {endpoint.Info.Model?.Id ?? config.Model}, pid {instance.ProcessId}");
        }
        catch (ProviderUnavailableException ex) when (ex.InnerException is ObjectDisposedException && IsDisposed)
        {
            throw new ObjectDisposedException(Name, ex);
        }
        catch (PluginUnavailableException ex)
        {
            // Its last lines often say why it stopped, such as a missing library.
            return HealthStatus.Error(ex.WithOutput(ex.Message), ex.Hint);
        }
        catch (ProviderUnavailableException ex)
        {
            return HealthStatus.Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nothing planned for, such as a registered folder that no path can name. The status says it to whoever asked;
            // the log has it too, which is all an unattended daemon's warm-up leaves. As a PluginUnavailableException, so
            // that a command that prints the status can keep the warning off its console.
            var message = $"STT '{id}' check failed: {TerminalText.Show(ex.Message)}";
            log.Warn(message, new PluginStartFailedException(message, ex));
            return HealthStatus.Error($"{Name} could not start: {TerminalText.Show(ex.Message)}");
        }
    }

    /// <summary>Lets go of the plugin's process; the host's next sweep stops it unless another provider holds it.</summary>
    public void Dispose()
    {
        PluginLease? lease;
        lock (_gate)
        {
            _disposed = true;
            lease = _lease;
            _lease = null;
        }

        lease?.Dispose();
    }

    /// <summary>
    /// The plugin's endpoint, once it is ready: resolved and leased on the first call, and started if it is not running.
    /// A resolution that failed is tried again on the next call.
    /// </summary>
    private async Task<(PluginLaunch Launch, PluginInstance Instance, PluginEndpoint Endpoint)> ReadyAsync(CancellationToken ct)
    {
        try
        {
            PluginLaunch launch;
            PluginInstance instance;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_lease is null)
                {
                    _launch = resolver.Resolve(config);
                    _lease = host.Acquire(_launch);
                }

                (launch, instance) = (_launch!, _lease.Instance);
            }

            return (launch, instance, await instance.ReadyAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Dictum stopped the plugin under this provider, as a shutdown does, or cancelled its start.
            throw Stopped(ex);
        }
    }

    private ProviderUnavailableException Stopped(Exception ex) => new($"{Name} was stopped while in use.", ex);

    /// <summary>
    /// The message for an error answer: its status, and its reason and the start of its body, which are the plugin's own
    /// text and so are spelt out.
    /// </summary>
    internal static string ErrorAnswer(string name, ServerAnswer answer) =>
        $"{name} answered {(int)answer.Status} {TerminalText.Show(answer.ReasonPhrase)}: {TerminalText.Show(OpenAiCompatible.Snippet(answer.Body))}";

    /// <summary>
    /// Protocol 1's form, OpenAI's with one field more: one language goes as <c>language</c>, a list to pick from as
    /// <c>languages</c>, and <c>auto</c> as neither. The model is the one configured, which the plugin already loaded.
    /// </summary>
    internal static MultipartFormDataContent BuildForm(string model, AudioClip clip, SttRequest request)
    {
        var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(Wav.ToPcm16Bytes(clip));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (Language(request) is { } language)
        {
            form.Add(new StringContent(language), "language");
        }
        else if (request.Language is null && request.Candidates is { Count: > 0 } candidates)
        {
            form.Add(new StringContent(string.Join(',', candidates)), "languages");
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            form.Add(new StringContent(request.Prompt), "prompt");
        }

        return form;
    }

    /// <summary>The one language the request names; none for <c>auto</c> or a list.</summary>
    private static string? Language(SttRequest request) =>
        request.Language is { Length: > 0 } language && language != LanguageList.Auto ? language : null;
}

/// <summary>A plugin's answer to a transcription: the text, and the language it heard when it says.</summary>
internal sealed record PluginTranscription(string? Text, string? Language);
