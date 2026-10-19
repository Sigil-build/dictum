using System.CommandLine;
using System.Text.Json;
using Dictum.Cli.Output;
using Dictum.Core.Configuration;
using Dictum.Core.Diagnostics;
using Dictum.Core.Vocabulary;
using Dictum.Host;
using Dictum.Host.Ipc;
using Dictum.Platform;
using Dictum.Setup;

namespace Dictum.Cli.Commands;

/// <summary>
/// Sets Dictum up: asks a few questions, downloads the chosen models (resumable, SHA-256 checked) and writes the
/// config last. Re-running it changes only what the answers change.
/// </summary>
internal static class InitCommand
{
    private const string CleanupSecret = "dictum-cleanup";
    private const string NoCleanup = "none";
    private const string GpuAuto = "auto";
    private const string GpuCpu = "cpu";
    private const string DefaultServerUrl = "http://localhost:8080/v1";

    public static Command Create()
    {
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask: use the options given and keep or recommend everything else" };
        var language = new Option<string?>("--language") { Description = "Language you dictate in: one code such as uk, several such as \"uk, en\" to pick from, or auto" };
        var stt = new Option<string?>("--stt") { Description = "Speech model id (see 'dictum models list')" };
        var llm = new Option<string?>("--llm") { Description = "Text cleanup: none, or the base URL of an OpenAI-compatible server, e.g. http://192.168.1.10:8080/v1" };
        var llmModel = new Option<string?>("--llm-model") { Description = "Model name on that server" };
        var llmApiKey = new Option<string?>("--llm-api-key") { Description = "API key for that server: env:VAR, secret:name, or the key itself (then kept in the OS secret store)" };
        var vocabFile = new Option<string?>("--vocab-file") { Description = "Words to add to the profile, one per line" };
        var autostart = new Option<bool>("--autostart") { Description = "Start the daemon at login" };
        var gpu = new Option<string?>("--gpu") { Description = "auto (default) or cpu" };
        gpu.AcceptOnlyFromAmong(GpuAuto, GpuCpu);
        var json = new Option<bool>("--json") { Description = "Print a JSON summary" };

        var command = new Command("init", "Set Dictum up: choose and download a speech model, cleanup and your words")
        {
            yes, language, stt, llm, llmModel, llmApiKey, vocabFile, autostart, gpu, json,
        };

        command.SetAction((parse, ct) => CliApp.RunAsync(parse, async (context, token) =>
        {
            var interactive = !parse.GetValue(yes);
            if (interactive && Console.IsInputRedirected)
            {
                throw new ConfigException("Input is not a terminal. Run 'dictum init --yes' with options; see 'dictum init --help'.");
            }

            var prompt = new ConsolePrompt(context.Error);
            var current = context.TryLoadConfig();
            var hardware = HardwareProbe.Detect(context.Paths);
            var catalog = Catalog.BuiltIn;
            var defaults = SetupPlanner.Defaults(current, hardware, catalog, m => File.Exists(context.Paths.ResolveModel(m)));
            context.Error.WriteLine(hardware.GpuName is null
                ? "No GPU that Whisper can use was found; recommending a model that is fast on a CPU."
                : $"GPU: {HardwareText.Gpu(hardware)}.");

            var languageAnswer = parse.GetValue(language)
                ?? (interactive ? AskLanguage(context, prompt, defaults.Language) : defaults.Language);
            if (!ConfigValidator.IsValidLanguage(languageAnswer))
            {
                throw new ConfigException($"'{languageAnswer}' is not an ISO 639-1 language code, a comma-separated list of codes, or 'auto'.");
            }

            if (LanguageList.Parse(languageAnswer) is { } codes && LanguageList.SimilarWarning(codes) is { } similar)
            {
                context.Error.WriteLine($"warning: {similar}");
            }

            var recommended = SetupPlanner.RecommendSpeechModel(hardware);
            var sttAnswer = parse.GetValue(stt)
                ?? (interactive ? ChooseModel(prompt, catalog, languageAnswer, defaults, recommended) : defaults.SpeechModel);

            var (server, pendingSecret) = AskCleanup(context, prompt, interactive, parse.GetValue(llm), parse.GetValue(llmModel), parse.GetValue(llmApiKey), defaults.Server);

            var words = new List<string>();
            if (parse.GetValue(vocabFile) is { } file)
            {
                words.AddRange(VocabularyList.ParseLines(await File.ReadAllTextAsync(file, token).ConfigureAwait(false)));
            }

            if (interactive)
            {
                var typed = prompt.Ask("Names, apps and terms the recognizer gets wrong, comma-separated (Enter to skip)", null);
                words.AddRange(typed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }

            var autostartAnswer = AskAutostart(context, prompt, interactive, parse.GetValue(autostart));

            var answers = new SetupAnswers(
                languageAnswer,
                sttAnswer,
                server is null ? CleanupChoice.None : CleanupChoice.ExistingServer,
                server,
                words,
                ForceCpu: parse.GetValue(gpu) == GpuCpu);
            var plan = SetupPlanner.Plan(current, answers, catalog, e => ModelFiles.IsInstalled(context.Paths, e));

            if (plan.Downloads.Count == 0)
            {
                context.Error.WriteLine("All model files are already in place.");
            }
            else
            {
                context.Error.WriteLine($"Dictum will download {SetupRunner.Format(plan.DownloadBytes)}:");
                foreach (var download in plan.Downloads)
                {
                    context.Error.WriteLine($"  {download.Title}, {SetupRunner.Format(download.Size)}, {download.Licence.Name} licence");
                    context.Error.WriteLine($"    from {download.Url}");
                }

                if (interactive && !prompt.Confirm("Continue?", defaultValue: true))
                {
                    context.Error.WriteLine("Nothing was changed.");
                    return ExitCodes.Success;
                }
            }

            if (server is not null)
            {
                await WarnIfUnreachableAsync(context, plan.Config, pendingSecret, token).ConfigureAwait(false);
            }

            using (var http = FileDownloader.CreateHttpClient(CliApp.UserAgent))
            using (var view = new SetupProgressView(context.Error, redraw: !Console.IsErrorRedirected))
            {
                await new SetupRunner(new FileDownloader(http), context.Paths)
                    .RunAsync(plan, context.Store, hardware.FreeDiskBytes, view, token, beforeSave: pendingSecret is null
                        ? null
                        : c => context.Platform.Secrets.SetAsync(CleanupSecret, pendingSecret, c)).ConfigureAwait(false);
            }

            await ApplyAutostartAsync(context, autostartAnswer, token).ConfigureAwait(false);
            await ReportReadinessAsync(context, autostartAnswer, token).ConfigureAwait(false);
            context.Error.WriteLine($"Add words any time: dictum vocab add --profile {plan.Config.Profiles[0].Id} \"Notepad++\" \"VS Code\"");

            context.Out.WriteLine(parse.GetValue(json)
                ? JsonSerializer.Serialize(
                    new InitJson(context.ConfigPath, sttAnswer ?? defaults.KeptModelFile ?? "", [.. plan.Downloads.Select(d => d.Id)], server?.BaseUrl ?? NoCleanup, autostartAnswer),
                    CliJsonContext.Readable.InitJson)
                : context.ConfigPath);
            return ExitCodes.Success;
        }, ct));

        return command;
    }

    private static string AskLanguage(CliContext context, ConsolePrompt prompt, string defaultValue)
    {
        while (true)
        {
            var answer = prompt.Ask("Language you dictate in: one code such as uk, several such as uk, en to pick from on each dictation, or auto", defaultValue);
            if (ConfigValidator.IsValidLanguage(answer))
            {
                return answer;
            }

            context.Error.WriteLine($"'{answer}' is not a language setting. Enter one code such as uk, codes separated by commas such as uk, en, or auto.");
        }
    }

    /// <summary>Returns a catalog id, or null to keep the current model file.</summary>
    private static string? ChooseModel(ConsolePrompt prompt, Catalog catalog, string language, SetupDefaults defaults, string recommended)
    {
        var models = catalog.Entries.Where(e => e is { Kind: CatalogKind.Whisper, Hidden: false } && e.Understands(language)).ToList();
        var options = models.Select(m => $"{m.Title}, {SetupRunner.Format(m.Size)}{(m.Id == recommended ? " (recommended)" : "")}").ToList();
        if (defaults.KeptModelFile is { } kept)
        {
            options.Insert(0, $"Keep {kept} (current)");
            var choice = prompt.Choose("Speech model:", options, 0);
            return choice == 0 ? null : models[choice - 1].Id;
        }

        var index = Math.Max(0, models.FindIndex(m => m.Id == (defaults.SpeechModel ?? recommended)));
        return models[prompt.Choose("Speech model:", options, index)].Id;
    }

    /// <summary>Returns the server to use (null for no cleanup) and a key value still to be written to the secret store.</summary>
    private static (ExistingLlmServer? Server, string? PendingSecret) AskCleanup(
        CliContext context,
        ConsolePrompt prompt,
        bool interactive,
        string? llm,
        string? llmModel,
        string? llmApiKey,
        ExistingLlmServer? current)
    {
        if (llm is not null)
        {
            if (llm == NoCleanup)
            {
                return (null, null);
            }

            var model = llmModel ?? current?.Model ?? throw new ConfigException("Pass --llm-model with --llm <url>.");
            var (key, pending) = llmApiKey is null ? (SetupPlanner.CarriedApiKey(current, llm), null) : KeepKeyOutOfConfig(context, llmApiKey);
            return (new ExistingLlmServer(llm, model, key), pending);
        }

        if (!interactive)
        {
            return (current, null);
        }

        var choice = prompt.Choose(
            "Clean up punctuation and typos with a language model?",
            ["No", "Yes, with a server I already run (llama-server, llama-swap, Ollama or any OpenAI-compatible API)"],
            current is null ? 0 : 1);
        if (choice == 0)
        {
            return (null, null);
        }

        var baseUrl = prompt.Ask("Server URL, including /v1", current?.BaseUrl ?? DefaultServerUrl);
        while (!ConfigValidator.IsHttpUrl(baseUrl))
        {
            context.Error.WriteLine($"'{baseUrl}' is not a server URL; it starts with http:// or https://, e.g. http://localhost:8080/v1.");
            baseUrl = prompt.Ask("Server URL, including /v1", current?.BaseUrl ?? DefaultServerUrl);
        }
        var modelName = prompt.Ask("Model name on that server", current?.Model);
        var apiKey = SetupPlanner.CarriedApiKey(current, baseUrl);
        string? pendingSecret = null;
        var typed = prompt.ReadHidden(apiKey is null
            ? "API key (Enter if the server needs none): "
            : "API key (Enter keeps the current one, - for none): ");
        if (typed == "-")
        {
            apiKey = null;
        }
        else if (typed.Length > 0)
        {
            try
            {
                (apiKey, pendingSecret) = KeepKeyOutOfConfig(context, typed);
            }
            catch (ConfigException ex)
            {
                context.Error.WriteLine($"warning: {ex.Message}");
            }
        }

        return (new ExistingLlmServer(baseUrl, modelName, apiKey), pendingSecret);
    }

    /// <summary>
    /// A key given as a value never goes into config.json (which may roam between machines): the config gets
    /// <c>secret:dictum-cleanup</c>, and the value is written to the OS secret store only once the downloads and
    /// validation succeeded, right before the config is saved.
    /// </summary>
    private static (string Reference, string? PendingSecret) KeepKeyOutOfConfig(CliContext context, string key)
    {
        if (ConfigValueResolver.IsReference(key))
        {
            return (key, null);
        }

        if (context.Platform.Secrets is UnavailableSecretStore)
        {
            throw new ConfigException(
                "This system has no secret store Dictum can use, and the key is not written to the config in plain text. Pass it as env:VAR instead.");
        }

        return ($"secret:{CleanupSecret}", key);
    }

    private static bool? AskAutostart(CliContext context, ConsolePrompt prompt, bool interactive, bool flag)
    {
        if (context.Platform.Autostart is not { } autostart)
        {
            return null;
        }

        if (flag)
        {
            return true;
        }

        if (!interactive)
        {
            return null;
        }

        if (PathWarning() is { } warning)
        {
            context.Error.WriteLine(warning);
        }

        return prompt.Confirm("Start Dictum when you log in?", autostart.Installed is not null);
    }

    private static async Task ApplyAutostartAsync(CliContext context, bool? enable, CancellationToken ct)
    {
        if (enable is not { } on || context.Platform.Autostart is not { } autostart)
        {
            return;
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the dictum executable path.");
        var item = LoginItem.NextTo(executable, InstallLocation.AppImage);
        if (await autostart.ApplyAsync(on, item, ct).ConfigureAwait(false))
        {
            context.Error.WriteLine(on ? $"Dictum will start at login ({autostart.Location(item.App)})." : "Dictum will no longer start at login.");
        }
    }

    /// <summary>A <paramref name="pendingSecret"/> is not in the secret store yet, so the check uses a copy of the config that holds it.</summary>
    private static async Task WarnIfUnreachableAsync(CliContext context, DictumConfig config, string? pendingSecret, CancellationToken ct)
    {
        var id = SetupPlanner.CleanupProcessorId(config);
        if (pendingSecret is not null && config.Processors[id] is LlmProcessorConfig llm)
        {
            config = ConfigStore.Parse(ConfigStore.Serialize(config));
            config.Processors[id] = llm with { ApiKey = pendingSecret };
        }

        await using var host = context.CreateHost(config);
        if (host.GetProcessor(id) is IHealthCheck check)
        {
            var status = await check.CheckAsync(ct).ConfigureAwait(false);
            if (status.Level != HealthLevel.Ok)
            {
                context.Error.WriteLine($"warning: {status.Detail}. The setting is saved anyway; cleanup is skipped while the server is unreachable.");
            }
        }
    }

    private static async Task ReportReadinessAsync(CliContext context, bool? autostart, CancellationToken ct)
    {
        bool running;
        try
        {
            await new IpcClient(context.SocketPath).SendAsync(new IpcRequest(IpcCommand.Status), ct).ConfigureAwait(false);
            running = true;
        }
        catch (DaemonNotRunningException)
        {
            running = false;
        }

        context.Error.WriteLine(running
            ? "Done. The running daemon picks up the new config by itself; 'dictum doctor' shows the details."
            : autostart == true || context.Platform.Autostart?.Installed is not null
                ? "Done. Dictum starts at your next login; to start it now, run 'dictum daemon'."
                : "Done. Start dictation with 'dictum daemon'.");
    }

    /// <summary>
    /// Login start records the executable path, so it breaks when Dictum runs from a folder that gets cleaned up. Inside an
    /// AppImage that is the AppImage file, not its temporary mount.
    /// </summary>
    private static string? PathWarning()
    {
        if (Environment.ProcessPath is not { } running)
        {
            return null;
        }

        var executable = InstallLocation.Lasting(running, InstallLocation.AppImage);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return executable.StartsWith(downloads, comparison) || executable.StartsWith(Path.GetTempPath(), comparison)
            ? $"warning: Dictum runs from {Path.GetDirectoryName(executable)}; login start breaks if that folder is moved or cleaned. Move Dictum to a permanent folder first."
            : null;
    }
}
